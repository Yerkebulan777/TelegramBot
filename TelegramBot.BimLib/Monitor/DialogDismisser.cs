using Microsoft.Extensions.Logging;
using TelegramBot.BimLib.Native;

namespace TelegramBot.BimLib.Monitor;

/// <summary>
/// Автоматическое закрытие диалоговых окон Revit (#32770).
/// Ищет кнопки с известными названиями (ОК, Отмена, Закрыть и т.д.) и кликает их.
/// </summary>
internal sealed class DialogDismisser(ILogger<DialogDismisser> logger)
{
    private static readonly string[] ButtonNameTexts =
        ["OK", "ОК", "Принять", "Accept", "Закрыть", "Close", "Игнорировать", "Ignore",
         "Отмена", "Cancel", "Нет", "No", "Да", "Yes", "Продолжить", "Continue",
         "Не сохранять", "Don't Save", "Сохранить", "Save"];

    private static readonly string[] ExclusionDialogTitles =
        ["Информация", "Information", "Справка", "Help"];

    /// <summary>
    /// Проверяет и закрывает диалоговые окна для указанного процесса.
    /// Возвращает true, если хотя бы один диалог был закрыт.
    /// </summary>
    internal bool DismissDialogsForProcess(uint processId)
    {
        var dialogs = FindEnabledDialogs(processId);
        if (dialogs.Count == 0) return false;

        var dismissed = false;

        foreach (var hwndDlg in dialogs)
        {
            var info = WindowInfo.FromHandle(hwndDlg);
            logger.LogDebug("Dialog detected: {Info}", info);

            if (IsExcluded(info.WindowTitle))
            {
                logger.LogDebug("Dialog excluded: {Title}", info.WindowTitle);
                continue;
            }

            // Пробуем найти и кликнуть известную кнопку
            if (TryClickKnownButton(hwndDlg))
            {
                logger.LogInformation("Dialog dismissed: {Title} via known button", info.WindowTitle);
                dismissed = true;
                continue;
            }

            // Fallback: кликаем первую доступную кнопку
            if (TryClickFirstButton(hwndDlg))
            {
                logger.LogInformation("Dialog dismissed: {Title} via fallback", info.WindowTitle);
                dismissed = true;
            }
        }

        return dismissed;
    }

    /// <summary>
    /// Находит все включённые (enabled) диалоговые окна (#32770) для указанного процесса.
    /// </summary>
    private static List<IntPtr> FindEnabledDialogs(uint processId)
    {
        var dialogs = new List<IntPtr>();

        User32.EnumWindows((hwnd, _) =>
        {
            var className = WindowUtil.GetWindowClassName(hwnd);

            // #32770 — стандартный класс диалоговых окон
            if (className != "#32770")
                return true;

            var actualPid = WindowUtil.GetWindowProcessId(hwnd);
            if (actualPid != processId)
                return true;

            if (!User32.IsWindowEnabled(hwnd))
                return true;

            dialogs.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        return dialogs;
    }

    /// <summary>Проверяет, исключён ли заголовок диалога из автозакрытия.</summary>
    private static bool IsExcluded(string title)
    {
        return ExclusionDialogTitles.Any(ex =>
            title.Contains(ex, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Ищет кнопку с известным текстом и кликает её.</summary>
    private static bool TryClickKnownButton(IntPtr hwndDlg)
    {
        // Ищем кнопки внутри диалога
        var buttons = WindowUtil.EnumerateChildWindows(hwndDlg, "Button");
        if (buttons.Count == 0) return false;

        foreach (var hwndBtn in buttons)
        {
            var btnText = WindowUtil.GetWindowTitle(hwndBtn);
            if (string.IsNullOrEmpty(btnText)) continue;

            var cleanText = btnText.Replace("&", "").Trim();

            if (ButtonNameTexts.Any(name =>
                string.Equals(cleanText, name, StringComparison.OrdinalIgnoreCase)))
            {
                WindowUtil.SendButtonClick(hwndBtn);
                return true;
            }
        }

        return false;
    }

    /// <summary>Fallback: кликает первую доступную кнопку.</summary>
    private static bool TryClickFirstButton(IntPtr hwndDlg)
    {
        var buttons = WindowUtil.EnumerateChildWindows(hwndDlg, "Button");
        if (buttons.Count == 0) return false;

        // Пропускаем кнопки с BNS_BUSY
        foreach (var hwndBtn in buttons)
        {
            if (!User32.IsWindowEnabled(hwndBtn))
                continue;

            WindowUtil.SendButtonClick(hwndBtn);
            return true;
        }

        return false;
    }
}
