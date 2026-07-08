using System.Text;
using TelegramBot.Worker.BimLib.Helpers;

namespace TelegramBot.Worker.BimLib.Native;

/// <summary>Win32-утилиты: поиск окон, получение информации, клики.</summary>
internal static class WindowUtil
{

    /// <summary>Получает заголовок окна по HWND. Возвращает <see cref="string.Empty"/> при ошибке.</summary>
    internal static string GetWindowTitle(IntPtr hwnd)
    {
        return User32.GetWindowTextSafe(hwnd) ?? string.Empty;
    }

    /// <summary>Получает имя класса окна по HWND. Возвращает <see cref="string.Empty"/> при ошибке.</summary>
    internal static string GetWindowClassName(IntPtr hwnd)
    {
        return User32.GetClassNameSafe(hwnd) ?? string.Empty;
    }

    /// <summary>Получает ID процесса, которому принадлежит окно. Возвращает 0 при ошибке.</summary>
    internal static uint GetWindowProcessId(IntPtr hwnd)
    {
        return User32.GetWindowThreadProcessIdSafe(hwnd);
    }

    /// <summary>
    /// Перечисляет все top-level окна, соответствующие указанным критериям.
    /// </summary>
    internal static List<IntPtr> GetTopLevelWindows(string? className = null, string? windowTitle = null, uint? processId = null)
    {
        var result = new List<IntPtr>();

        _ = User32.EnumWindowsSafe((hwnd, _) =>
        {
            try
            {
                if (!User32.IsWindowVisibleSafe(hwnd))
                {
                    return true;
                }

                if (className != null)
                {
                    var actualClass = GetWindowClassName(hwnd);
                    if (!string.Equals(actualClass, className, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                if (windowTitle != null)
                {
                    var actualTitle = GetWindowTitle(hwnd);
                    if (!actualTitle.Contains(windowTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                if (processId.HasValue)
                {
                    var actualPid = GetWindowProcessId(hwnd);
                    if (actualPid != processId.Value)
                    {
                        return true;
                    }
                }

                result.Add(hwnd);
            }
            catch (Exception ex)
            {
                WinApiHelper.LogError("EnumWindowsCallback", ex, $"hwnd={hwnd}");
            }

            return true; // always continue enumeration
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>
    /// Перечисляет дочерние окна указанного родителя, соответствующие критериям.
    /// </summary>
    internal static List<IntPtr> EnumerateChildWindows(IntPtr parentHwnd, string? className = null, string? windowTitle = null)
    {
        var result = new List<IntPtr>();

        _ = User32.EnumChildWindowsSafe(parentHwnd, (hwnd, _) =>
        {
            try
            {
                if (className != null)
                {
                    var actualClass = GetWindowClassName(hwnd);
                    if (!string.Equals(actualClass, className, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                if (windowTitle != null)
                {
                    var actualTitle = GetWindowTitle(hwnd);
                    if (!string.Equals(actualTitle, windowTitle, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                result.Add(hwnd);
            }
            catch (Exception ex)
            {
                WinApiHelper.LogError("EnumChildWindowsCallback", ex, $"parent={parentHwnd}, hwnd={hwnd}");
            }

            return true; // always continue enumeration
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>
    /// Отправляет клик по кнопке через PostMessage (асинхронно, не блокируется).
    /// Использует PostMessage вместо SendMessage, чтобы не подвисать на модальных диалогах Revit.
    /// </summary>
    internal static void SendButtonClick(IntPtr hwndButton)
    {
        try
        {
            // PostMessage(BM_CLICK) — асинхронно, не ждёт обработки очередью диалога
            _ = User32.PostMessageSafe(hwndButton, Win32Consts.BmClick, IntPtr.Zero, IntPtr.Zero);

            // Если кнопка всё ещё enabled — дополнительно имитируем нажатие через PostMessage
            if (!User32.IsWindowEnabledSafe(hwndButton))
            {
                return;
            }

            // Fallback: имитация нажатия (асинхронно, PostMessage)
            _ = User32.PostMessageSafe(hwndButton, Win32Consts.BmSetState, 1, IntPtr.Zero);
            _ = User32.PostMessageSafe(hwndButton, Win32Consts.WmLButtonDown, IntPtr.Zero, IntPtr.Zero);
            _ = User32.PostMessageSafe(hwndButton, Win32Consts.WmLButtonUp, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            WinApiHelper.LogError(nameof(SendButtonClick), ex, $"hWnd={hwndButton}");
        }
    }

    /// <summary>
    /// Отправляет WM_COMMAND + BN_CLICKED родителю диалога — стандартный Win32 способ нажатия кнопки.
    /// Многие кастомные контролы (RevitBitmapButton и др.) не обрабатывают BM_CLICK,
    /// но обязаны реагировать на WM_COMMAND с BN_CLICKED.
    /// </summary>
    internal static void SendButtonCommandClick(IntPtr hwndDlg, IntPtr hwndButton)
    {
        try
        {
            var ctrlId = User32.GetWindowLongSafe(hwndButton, Win32Consts.GWL_ID);
            // HIWORD(wParam) = BN_CLICKED (0), LOWORD(wParam) = control ID
            var wParam = new IntPtr((ctrlId & 0xFFFF) | (Win32Consts.BnClicked << 16));
            _ = User32.PostMessageSafe(hwndDlg, Win32Consts.WmCommand, wParam, hwndButton);
        }
        catch (Exception ex)
        {
            WinApiHelper.LogError(nameof(SendButtonCommandClick), ex,
                $"dlg={hwndDlg}, btn={hwndButton}");
        }
    }

    /// <summary>
    /// Логирует все дочерние окна указанного родителя с их классами, заголовками и состоянием.
    /// Используется для диагностики, почему кнопки не находятся/не нажимаются.
    /// </summary>
    internal static void LogAllChildWindows(ILogger logger, IntPtr hwndDlg, string context)
    {
        const int maxDetailsLength = 4096;

        if (!logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        try
        {
            var allChildren = EnumerateChildWindows(hwndDlg);
            var buttons = EnumerateChildWindows(hwndDlg, "Button");
            var details = new StringBuilder()
                .Append("dialog=").Append(hwndDlg)
                .Append(", children=").Append(allChildren.Count)
                .Append(", buttons=").Append(buttons.Count);

            foreach (var child in allChildren)
            {
                _ = details
                    .AppendLine()
                    .Append("child=").Append(child)
                    .Append(", class=").Append(GetWindowClassName(child))
                    .Append(", title=").Append(GetWindowTitle(child))
                    .Append(", enabled=").Append(User32.IsWindowEnabledSafe(child))
                    .Append(", visible=").Append(User32.IsWindowVisibleSafe(child))
                    .Append(", controlId=").Append(User32.GetWindowLongSafe(child, Win32Consts.GWL_ID));

                if (details.Length >= maxDetailsLength)
                {
                    _ = details.AppendLine().Append("...");
                    break;
                }
            }

            logger.LogDebug("Dialog diag: ctx={Context}, details={Details}", context, details.ToString());
        }
        catch (Exception ex)
        {
            WinApiHelper.LogError(nameof(LogAllChildWindows), ex, $"hWnd={hwndDlg}");
        }
    }

}
