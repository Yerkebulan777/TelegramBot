using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics;
using TelegramBot.BimLib.Config;
using TelegramBot.BimLib.Helpers;
using TelegramBot.BimLib.Models;
using TelegramBot.BimLib.Native;

namespace TelegramBot.BimLib.Monitor;

/// <summary>
/// Автоматическое закрытие диалоговых окон Revit (#32770).
/// Закрытие — 3 стратегии:
/// <list type="number">
///   <item>Клик известной кнопки по тексту (CloseButtonTexts) среди Button-контролов</item>
///   <item>WM_CLOSE + WM_SYSCOMMAND + SC_CLOSE</item>
///   <item>Запрос kill tracked-процесса через ProcessRunner (после MaxDismissAttempts)</item>
/// </list>
/// </summary>
public sealed class DialogDismisser(ILogger<DialogDismisser> logger, IOptions<DialogDismisserOptions> optionsAccessor)
{
    private readonly ILogger<DialogDismisser> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly DialogDismisserOptions _options = optionsAccessor.Value;
    private readonly ConcurrentDictionary<uint, int> _dismissAttempts = new();

    private const string _dialogWindowClass = "#32770";

    /// <summary>
    /// Проверяет и закрывает диалоговые окна для указанного процесса.
    /// Возвращает true, если ProcessRunner должен убить tracked-процесс.
    /// </summary>
    public bool NeedsProcessKillAfterDismiss(uint processId)
    {
        var dialogs = FindDialogs(processId);
        if (dialogs.Count == 0)
        {
            // Диалогов нет — сбрасываем счётчик попыток
            _=_dismissAttempts.TryRemove(processId, out _);
            return false;
        }

        // Логируем ВСЕ найденные диалоги + их дочерние окна для диагностики
        foreach (var hwndDlg in dialogs)
        {
            var info = WindowInfo.FromHandle(hwndDlg);
            _logger.LogDebug("Dialog: {Info}", info);
            WindowUtil.LogAllChildWindows(_logger, hwndDlg, $"DialogDismisser PID={processId}");
        }

        var dismissed = false;

        foreach (var hwndDlg in dialogs)
        {
            var info = WindowInfo.FromHandle(hwndDlg);

            if (IsExcluded(info.WindowTitle))
            {
                _logger.LogDebug("Dialog excluded: '{Title}'", info.WindowTitle);
                continue;
            }

            // Содержимое диалога — единственный способ понять, ЧТО Revit показал
            // (у многих диалогов пустой заголовок, а Debug-уровень обычно выключен).
            var content = DescribeDialogContent(hwndDlg);

            if (TryClickKnownDialogButton(hwndDlg, info.WindowTitle, out var dialogButton))
            {
                _logger.LogInformation("Dialog dismissed: title='{Title}', btn='{Button}', pid={Pid}, strategy=title",
                    info.WindowTitle, dialogButton, processId);
                dismissed = true;
                continue;
            }

            // Стратегия 1: поиск и клик по известному тексту кнопки
            if (TryClickKnownButton(hwndDlg, out var knownButton))
            {
                _logger.LogInformation("Dialog dismissed: title='{Title}', btn='{Button}', pid={Pid}", info.WindowTitle, knownButton, processId);
                dismissed = true;
                continue;
            }

            // Стратегия 2: WM_CLOSE + WM_SYSCOMMAND + SC_CLOSE
            if (TryCloseDialogViaWindowMessage(hwndDlg))
            {
                _logger.LogInformation("Dialog dismissed: title='{Title}', pid={Pid}, strategy=WM_CLOSE", info.WindowTitle, processId);
                dismissed = true;
            }
            else
            {
                _logger.LogWarning("Cannot dismiss dialog: {Info}, content={Content}", info, content);
            }
        }

        if (dismissed)
        {
            // Успешно закрыли — сбрасываем счётчик
            _=_dismissAttempts.TryRemove(processId, out _);
        }
        else if (_options.MaxDismissAttempts > 0)
        {
            // Диалоги есть, но закрыть не удалось — учитываем попытку
            var attempts = _dismissAttempts.AddOrUpdate(processId, 1, (_, count) => count + 1);
            _logger.LogWarning("Dismiss fail: pid={ProcessId} (attempt {Attempts}/{Max})", processId, attempts, _options.MaxDismissAttempts);

            if (attempts >= _options.MaxDismissAttempts)
            {
                _logger.LogWarning(
                    "Dismiss fail limit: pid={ProcessId} after {Max} attempts; requesting tracked kill",
                    processId, _options.MaxDismissAttempts);
                _ = _dismissAttempts.TryRemove(processId, out _);
                return true;
            }
        }
        else
        {
            // MaxDismissAttempts == 0 — авто-kill отключён, логируем без счётчика
            _logger.LogWarning("Dismiss fail: pid={ProcessId} (auto-kill off)", processId);
        }

        return false;
    }

    /// <summary>
    /// Ищет enabled Revit-диалоги класса #32770 для указанного процесса.
    /// </summary>
    private static List<IntPtr> FindDialogs(uint processId)
    {
        var mainWindow = GetMainWindowHandle(processId);

        return WindowUtil.GetTopLevelWindows(
            className: _dialogWindowClass,
            processId: processId)
            .Where(hwnd => hwnd != mainWindow)
            .Where(User32.IsWindowEnabledSafe)
            .OrderBy(WindowUtil.GetWindowTitle)
            .ToList();
    }

    /// <summary>Получает handle главного окна процесса. Возвращает IntPtr.Zero при ошибке.</summary>
    private static IntPtr GetMainWindowHandle(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.MainWindowHandle;
        }
        catch (Exception ex)
        {
            WinApiHelper.LogError(nameof(GetMainWindowHandle), ex, $"processId={processId}");
            return IntPtr.Zero;
        }
    }

    /// <summary>Проверяет, исключён ли заголовок диалога из автозакрытия.</summary>
    private bool IsExcluded(string title)
    {
        return _options.ExclusionDialogTitles.Any(ex => title.Contains(ex, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Ищет кнопку с известным текстом среди Button-контролов.
    /// Для найденного контрола применяет BM_CLICK + WM_COMMAND + BN_CLICKED.
    /// </summary>
    private bool TryClickKnownButton(IntPtr hwndDlg, out string? clickedButtonText)
    {
        return TryClickButton(hwndDlg, _options.CloseButtonTexts, out clickedButtonText);
    }

    private bool TryClickKnownDialogButton(IntPtr hwndDlg, string title, out string? clickedButtonText)
    {
        clickedButtonText = null;

        string[]? buttons = title switch
        {
            var t when ContainsAny(t, "Changes Not Saved", "Cambios no guardados")
                => ["Do not save the project", "Don't Save", "No guardar el proyecto", "Не сохранять проект", "Не сохранять"],
            var t when ContainsAny(t, "Save File")
                => ["No", "Нет"],
            var t when ContainsAny(t, "Close Project Without Saving", "Editable Elements")
                => ["Relinquish all elements and worksets", "Relinquish elements and worksets", "Освободить все элементы и рабочие наборы"],
            var t when ContainsAny(t, "Local Changes Not Synchronized with Central")
                => ["Close the local file", "Закрыть локальный файл"],
            var t when ContainsAny(t, "Elements Lost on Import", "Navisworks NWC Exporter")
                => ["Close", "OK", "Закрыть", "ОК"],
            _ => null
        };

        return buttons != null && TryClickButton(hwndDlg, buttons, out clickedButtonText);
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private bool TryClickButton(IntPtr hwndDlg, IEnumerable<string> buttonTexts, out string? clickedButtonText)
    {
        clickedButtonText = null;

        var buttons = WindowUtil.EnumerateChildWindows(hwndDlg, "Button");
        if (buttons.Count == 0)
        {
            return false;
        }

        foreach (var name in buttonTexts)
        {
            foreach (var button in buttons)
            {
                if (!User32.IsWindowEnabledSafe(button))
                {
                    continue;
                }

                var cleanText = WindowUtil.GetWindowTitle(button).Replace("&", "").Trim();
                if (!string.Equals(cleanText, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _logger.LogDebug("Known button: text='{Text}', hwnd={Hwnd}, class='{Class}'", cleanText, button, cleanText);

                WindowUtil.SendButtonClick(button);
                WindowUtil.SendButtonCommandClick(hwndDlg, button);
                clickedButtonText = cleanText;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Краткое содержимое диалога: тексты дочерних контролов (static-текст сообщения, кнопки),
    /// склеенные через " | ", максимум 300 символов. Даёт понять, ЧТО показал Revit,
    /// даже когда заголовок диалога пуст.
    /// </summary>
    private static string DescribeDialogContent(IntPtr hwndDlg)
    {
        const int maxLength = 300;
        var parts = new List<string>();
        var total = 0;

        foreach (var child in WindowUtil.EnumerateChildWindows(hwndDlg))
        {
            var text = WindowUtil.GetWindowTitle(child);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var clean = text.Replace("&", "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            parts.Add(clean);
            total += clean.Length + 3;
            if (total >= maxLength)
            {
                parts.Add("...");
                break;
            }
        }

        return parts.Count == 0 ? "<empty>" : string.Join(" | ", parts);
    }

    /// <summary>
    /// Закрывает диалог через отправку WM_CLOSE и WM_SYSCOMMAND + SC_CLOSE.
    /// Используется как последняя попытка перед запросом kill через ProcessRunner.
    /// </summary>
    private static bool TryCloseDialogViaWindowMessage(IntPtr hwndDlg)
    {
        try
        {
            // Сначала пробуем WM_CLOSE
            _=User32.PostMessageSafe(hwndDlg, Win32Consts.WmClose, IntPtr.Zero, IntPtr.Zero);

            // Затем WM_SYSCOMMAND + SC_CLOSE (закрытие через системное меню)
            _=User32.PostMessageSafe(hwndDlg, Win32Consts.WmSysCommand,
                new IntPtr(Win32Consts.ScClose), IntPtr.Zero);

            // Ждём немного, чтобы проверить, закрылось ли окно
            Thread.Sleep(500);

            // Если окно всё ещё существует — считаем что не закрылось
            return !User32.IsWindowEnabledSafe(hwndDlg) && !User32.IsWindowVisibleSafe(hwndDlg);
        }
        catch (Exception ex)
        {
            WinApiHelper.LogError(nameof(TryCloseDialogViaWindowMessage), ex,
                $"hWnd={hwndDlg}");
            return false;
        }
    }
}
