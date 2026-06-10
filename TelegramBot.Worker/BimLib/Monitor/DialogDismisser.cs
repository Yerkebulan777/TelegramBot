using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using TelegramBot.Worker.BimLib.Config;
using TelegramBot.Worker.BimLib.Native;

namespace TelegramBot.Worker.BimLib.Monitor;

/// <summary>
/// Автоматическое закрытие диалоговых окон Revit (#32770).
/// Использует многоуровневую стратегию поиска:
/// <list type="number">
///   <item>Поиск top-level окон по известным заголовкам (KnownDialogPatterns)</item>
///   <item>Поиск окон класса #32770 (стандартный класс диалогов)</item>
///   <item>Поиск дочерних окон с кнопками Button</item>
/// </list>
/// Если диалог не удаётся закрыть за MaxDismissAttempts попыток — процесс завершается принудительно.
/// </summary>
public sealed class DialogDismisser(
    ILogger<DialogDismisser> logger,
    IOptions<DialogDismisserOptions> optionsAccessor)
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
            _dismissAttempts.TryRemove(processId, out _);
            return false;
        }

        // Логируем ВСЕ найденные диалоги для анализа
        foreach (var hwndDlg in dialogs)
        {
            var info = WindowInfo.FromHandle(hwndDlg);
            logger.LogDebug("Dialog detected: {Info}", info);
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

            // Стратегия 1: поиск и клик по известному тексту кнопки
            if (TryClickKnownButton(hwndDlg))
            {
                logger.LogInformation("Dialog dismissed: {Title} via known button", info.WindowTitle);
                dismissed = true;
                continue;
            }

            // Стратегия 2: клик первой доступной enabled кнопки (fallback)
            if (TryClickFirstButton(hwndDlg))
            {
                logger.LogInformation("Dialog dismissed: {Title} via fallback button", info.WindowTitle);
                dismissed = true;
            }
            else
            {
                logger.LogWarning("Cannot dismiss dialog: {Info} — no clickable buttons found", info);
            }
        }

        if (dismissed)
        {
            // Успешно закрыли — сбрасываем счётчик
            _dismissAttempts.TryRemove(processId, out _);
        }
        else
        {
            // Диалоги есть, но закрыть не удалось — учитываем попытку
            var attempts = _dismissAttempts.AddOrUpdate(processId, 1, (_, count) => count + 1);
            logger.LogWarning("Failed to dismiss dialogs for process {ProcessId} (attempt {Attempts}/{Max})",
                processId, attempts, _options.MaxDismissAttempts);

            if (_options.MaxDismissAttempts > 0 && attempts >= _options.MaxDismissAttempts)
            {
                KillProcess(processId);
            }
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

        // Стратегия A: top-level окна, чей заголовок содержит известные паттерны
        foreach (var pattern in _options.KnownDialogPatterns)
        {
            var byTitle = WindowUtil.GetTopLevelWindows(
                windowTitle: pattern,
                processId: processId);
            foreach (var w in byTitle)
            {
                _ = found.Add(w);
            }
        }

        // Стратегия B: окна стандартного класса диалогов #32770
        var byClass = WindowUtil.GetTopLevelWindows(
            className: DialogWindowClass,
            processId: processId);
        foreach (var w in byClass)
        {
            _ = found.Add(w);
        }

        // Стратегия C: дочерние окна с кнопками (диалоги, не попавшие в A/B)
        // Ищем top-level окна процесса, у которых есть дочерние Button
        var allProcessWindows = WindowUtil.GetTopLevelWindows(processId: processId);
        foreach (var w in allProcessWindows)
        {
            if (found.Contains(w))
            {
                continue;
            }

            var buttons = WindowUtil.EnumerateChildWindows(w, "Button");
            if (buttons.Count > 0)
            {
                _ = found.Add(w);
            }
        }

        // Фильтруем: оставляем только enabled окна
        return found
            .Where(hwnd => User32.IsWindowEnabled(hwnd))
            .OrderBy(WindowUtil.GetWindowTitle)
            .ToList();
    }

    /// <summary>Проверяет, исключён ли заголовок диалога из автозакрытия.</summary>
    private bool IsExcluded(string title)
    {
        return _options.ExclusionDialogTitles.Any(ex =>
            title.Contains(ex, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Ищет кнопку с известным текстом и кликает её.</summary>
    private bool TryClickKnownButton(IntPtr hwndDlg)
    {
        var buttons = WindowUtil.EnumerateChildWindows(hwndDlg, "Button");
        if (buttons.Count == 0)
        {
            return false;
        }

        foreach (var hwndBtn in buttons)
        {
            var btnText = WindowUtil.GetWindowTitle(hwndBtn);
            if (string.IsNullOrEmpty(btnText))
            {
                continue;
            }

            var cleanText = btnText.Replace("&", "").Trim();

            if (_options.CloseButtonTexts.Any(name =>
                string.Equals(cleanText, name, StringComparison.OrdinalIgnoreCase)))
            {
                WindowUtil.SendButtonClick(hwndBtn);
                return true;
            }
        }

        return false;
    }

    /// <summary>Fallback: кликает первую доступную enabled кнопку.</summary>
    private static bool TryClickFirstButton(IntPtr hwndDlg)
    {
        var buttons = WindowUtil.EnumerateChildWindows(hwndDlg, "Button");
        if (buttons.Count == 0)
        {
            return false;
        }

        foreach (var hwndBtn in buttons)
        {
            if (!User32.IsWindowEnabled(hwndBtn))
            {
                continue;
            }

            WindowUtil.SendButtonClick(hwndBtn);
            return true;
        }

        return false;
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
            _dismissAttempts.TryRemove(processId, out _);
        }
    }
}
