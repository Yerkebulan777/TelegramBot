using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics;
using TelegramBot.Worker.BimLib.Config;
using TelegramBot.Worker.BimLib.Helpers;
using TelegramBot.Worker.BimLib.Models;
using TelegramBot.Worker.BimLib.Native;

namespace TelegramBot.Worker.BimLib.Monitor;

/// <summary>
/// Автоматическое закрытие диалоговых окон Revit (#32770).
/// Закрытие — 3 стратегии:
/// <list type="number">
///   <item>Клик известной кнопки по тексту (CloseButtonTexts) среди Button-контролов</item>
///   <item>WM_CLOSE + WM_SYSCOMMAND + SC_CLOSE</item>
///   <item>Принудительное завершение процесса (после MaxDismissAttempts)</item>
/// </list>
/// </summary>
public sealed class DialogDismisser(ILogger<DialogDismisser> logger, IOptions<DialogDismisserOptions> optionsAccessor)
{
    private readonly DialogDismisserOptions _options = optionsAccessor.Value;
    private readonly ConcurrentDictionary<uint, int> _dismissAttempts = new();

    private const string DialogWindowClass = "#32770";

    /// <summary>
    /// Проверяет и закрывает диалоговые окна для указанного процесса.
    /// Возвращает true, если хотя бы один диалог был закрыт.
    /// </summary>
    internal bool DismissDialogsForProcess(uint processId)
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
            logger.LogDebug("Dialog detected: {Info}", info);
            WindowUtil.LogAllChildWindows(logger, hwndDlg, $"DialogDismisser PID={processId}");
        }

        var dismissed = false;

        foreach (var hwndDlg in dialogs)
        {
            var info = WindowInfo.FromHandle(hwndDlg);

            if (IsExcluded(info.WindowTitle))
            {
                logger.LogDebug("Dialog excluded: {Title}", info.WindowTitle);
                continue;
            }

            // Содержимое диалога — единственный способ понять, ЧТО Revit показал
            // (у многих диалогов пустой заголовок, а Debug-уровень обычно выключен).
            var content = DescribeDialogContent(hwndDlg);

            // Стратегия 1: поиск и клик по известному тексту кнопки
            if (TryClickKnownButton(hwndDlg, out var knownButton))
            {
                logger.LogInformation("Dialog dismissed: title='{Title}', button='{Button}', strategy=known, pid={Pid}, content=[{Content}]",
                    info.WindowTitle, knownButton, processId, content);
                dismissed = true;
                continue;
            }

            // Стратегия 2: WM_CLOSE + WM_SYSCOMMAND + SC_CLOSE
            if (TryCloseDialogViaWindowMessage(hwndDlg))
            {
                logger.LogInformation("Dialog dismissed: title='{Title}', strategy=WM_CLOSE, pid={Pid}, content=[{Content}]",
                    info.WindowTitle, processId, content);
                dismissed = true;
            }
            else
            {
                logger.LogWarning("Cannot dismiss dialog: {Info} — all strategies failed", info);
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
            logger.LogWarning("Failed to dismiss dialogs for process {ProcessId} (attempt {Attempts}/{Max})",
                processId, attempts, _options.MaxDismissAttempts);

            if (attempts >= _options.MaxDismissAttempts)
            {
                KillProcess(processId);
            }
        }
        else
        {
            // MaxDismissAttempts == 0 — авто-kill отключён, логируем без счётчика
            logger.LogWarning("Cannot dismiss dialogs for process {ProcessId} (auto-kill disabled)",
                processId);
        }

        return dismissed;
    }

    /// <summary>
    /// Ищет enabled Revit-диалоги класса #32770 для указанного процесса.
    /// </summary>
    private List<IntPtr> FindDialogs(uint processId)
    {
        var mainWindow = GetMainWindowHandle(processId);

        return WindowUtil.GetTopLevelWindows(
            className: DialogWindowClass,
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
        return _options.ExclusionDialogTitles.Any(ex =>
            title.Contains(ex, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Ищет кнопку с известным текстом среди Button-контролов.
    /// Для найденного контрола применяет BM_CLICK + WM_COMMAND + BN_CLICKED.
    /// </summary>
    private bool TryClickKnownButton(IntPtr hwndDlg, out string? clickedButtonText)
    {
        clickedButtonText = null;

        var buttons = WindowUtil.EnumerateChildWindows(hwndDlg, "Button");
        if (buttons.Count == 0)
        {
            return false;
        }

        foreach (var name in _options.CloseButtonTexts)
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

                logger.LogDebug(
                    "Known button found: text='{Text}', hwnd={Hwnd}, class='{Class}'",
                    cleanText, button, WindowUtil.GetWindowClassName(button));

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
    /// Используется как последняя попытка перед KillProcess.
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

    /// <summary>Принудительно завершает процесс по ID.</summary>
    private void KillProcess(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            logger.LogWarning("Killing process {ProcessId} ({ProcessName}) after {Max} failed dismiss attempts",
                processId, process.ProcessName, _options.MaxDismissAttempts);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to kill process {ProcessId}", processId);
        }
        finally
        {
            _=_dismissAttempts.TryRemove(processId, out _);
        }
    }
}
