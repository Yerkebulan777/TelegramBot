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
/// Использует многоуровневую стратегию поиска:
/// <list type="number">
///   <item>Поиск top-level окон по известным заголовкам (KnownDialogPatterns)</item>
///   <item>Поиск окон класса #32770 (стандартный класс диалогов)</item>
///   <item>Поиск top-level окон с любыми дочерними контролами</item>
/// </list>
/// Закрытие — 4 стратегии:
/// <list type="number">
///   <item>Клик известной кнопки по тексту (CloseButtonTexts) среди ВСЕХ дочерних окон</item>
///   <item>Клик первой enabled кнопки/контрола среди ВСЕХ дочерних окон</item>
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
        if (!_options.Enabled)
        {
            return false;
        }

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

            // Стратегия 1: поиск и клик по известному тексту кнопки (среди ВСЕХ child-окон)
            if (TryClickKnownButton(hwndDlg, out var knownButton))
            {
                logger.LogInformation("Dialog dismissed: title='{Title}', button='{Button}', strategy=known, pid={Pid}, content=[{Content}]",
                    info.WindowTitle, knownButton, processId, content);
                dismissed = true;
                continue;
            }

            // Стратегия 2: клик первой доступной enabled кнопки/контрола (среди ВСЕХ child-окон)
            if (TryClickFirstButton(hwndDlg, out var fallbackButton))
            {
                logger.LogInformation("Dialog dismissed: title='{Title}', button='{Button}', strategy=fallback, pid={Pid}, content=[{Content}]",
                    info.WindowTitle, fallbackButton, processId, content);
                dismissed = true;
                continue;
            }

            // Стратегия 3: WM_CLOSE + WM_SYSCOMMAND + SC_CLOSE
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
    /// Многоуровневый поиск диалоговых окон для указанного процесса.
    /// Комбинирует несколько стратегий для максимального покрытия.
    /// </summary>
    private List<IntPtr> FindDialogs(uint processId)
    {
        var found = new HashSet<IntPtr>();

        // Главное окно Revit нужно исключать из ВСЕХ стратегий поиска, а не только
        // из C: его заголовок ("ProjectName - Autodesk Revit 2023") содержит подстроку
        // "Autodesk Revit" из KnownDialogPatterns, поэтому Strategy A ловила его как
        // диалог и закрывала через WM_CLOSE/SC_CLOSE — фактически завершая Revit.
        var mainWindow = GetMainWindowHandle(processId);

        // Стратегия A: top-level окна, чей заголовок содержит известные паттерны
        foreach (var pattern in _options.KnownDialogPatterns)
        {
            var byTitle = WindowUtil.GetTopLevelWindows(
                windowTitle: pattern,
                processId: processId);
            foreach (var w in byTitle)
            {
                if (w != mainWindow)
                {
                    _ = found.Add(w);
                }
            }
        }

        // Стратегия B: окна стандартного класса диалогов #32770
        var byClass = WindowUtil.GetTopLevelWindows(
            className: DialogWindowClass,
            processId: processId);
        foreach (var w in byClass)
        {
            if (w != mainWindow)
            {
                _ = found.Add(w);
            }
        }

        // Стратегия C: top-level окна с любыми дочерними контролами (не только Button)
        // Family Editor и кастомные Revit-диалоги могут использовать классы
        // отличные от "Button" (RevitBitmapButton, ToolbarWindow32 и т.д.)
        var allProcessWindows = WindowUtil.GetTopLevelWindows(processId: processId);
        foreach (var w in allProcessWindows)
        {
            if (found.Contains(w) || w == mainWindow)
            {
                continue;
            }

            // Ищем ЛЮБЫЕ дочерние окна с непустым текстом — признак кликабельного контрола
            var allChildren = WindowUtil.EnumerateChildWindows(w);
            var hasClickableChildren = allChildren.Any(child =>
            {
                var text = WindowUtil.GetWindowTitle(child);
                return !string.IsNullOrEmpty(text);
            });

            if (hasClickableChildren)
            {
                _ = found.Add(w);
            }
        }

        // Фильтруем: оставляем только enabled окна
        return found
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
    /// Ищет кнопку/контрол с известным текстом среди ВСЕХ дочерних окон (не только "Button").
    /// Для найденного контрола применяет BM_CLICK + WM_COMMAND + BN_CLICKED.
    /// </summary>
    private bool TryClickKnownButton(IntPtr hwndDlg, out string? clickedButtonText)
    {
        clickedButtonText = null;

        // Ищем среди ВСЕХ дочерних окон, не только класса "Button"
        var allChildren = WindowUtil.EnumerateChildWindows(hwndDlg);
        if (allChildren.Count == 0)
        {
            return false;
        }

        foreach (var child in allChildren)
        {
            var childText = WindowUtil.GetWindowTitle(child);
            if (string.IsNullOrEmpty(childText))
            {
                continue;
            }

            var cleanText = childText.Replace("&", "").Trim();

            if (_options.CloseButtonTexts.Any(name =>
                string.Equals(cleanText, name, StringComparison.OrdinalIgnoreCase)))
            {
                logger.LogDebug(
                    "Known button found: text='{Text}', hwnd={Hwnd}, class='{Class}'",
                    cleanText, child, WindowUtil.GetWindowClassName(child));

                // Отправляем оба типа клика для максимальной совместимости
                WindowUtil.SendButtonClick(child);
                WindowUtil.SendButtonCommandClick(hwndDlg, child);
                clickedButtonText = cleanText;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Fallback: кликает первый доступный enabled дочерний контрол с непустым текстом.
    /// Ищет среди ВСЕХ классов окон (не только "Button").
    /// </summary>
    private static bool TryClickFirstButton(IntPtr hwndDlg, out string? clickedButtonText)
    {
        clickedButtonText = null;

        var allChildren = WindowUtil.EnumerateChildWindows(hwndDlg);
        if (allChildren.Count == 0)
        {
            return false;
        }

        // Сначала ищем enabled контролы с непустым текстом
        foreach (var child in allChildren)
        {
            if (!User32.IsWindowEnabledSafe(child))
            {
                continue;
            }

            var text = WindowUtil.GetWindowTitle(child);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            WindowUtil.SendButtonClick(child);
            WindowUtil.SendButtonCommandClick(hwndDlg, child);
            clickedButtonText = text.Replace("&", "").Trim();
            return true;
        }

        // Если ни один с текстом не найден — кликаем первый enabled (любой)
        foreach (var child in allChildren)
        {
            if (!User32.IsWindowEnabledSafe(child))
            {
                continue;
            }

            WindowUtil.SendButtonClick(child);
            WindowUtil.SendButtonCommandClick(hwndDlg, child);
            clickedButtonText = "<no text>";
            return true;
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
