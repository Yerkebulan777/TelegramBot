using TelegramBot.Worker.BimLib.Native;

namespace TelegramBot.Worker.BimLib.Monitor;

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

    /// <summary>Отправляет клик по кнопке (BM_CLICK или fallback) с защитой от зависания.</summary>
    internal static void SendButtonClick(IntPtr hwndButton)
    {
        try
        {
            // Пробуем BM_CLICK (с таймаутом)
            _ = User32.SendMessageSafe(hwndButton, Win32Consts.BmClick, IntPtr.Zero, IntPtr.Zero);

            // Если кнопка не реагирует — пробуем установить состояние и отправить LBUTTON
            if (!User32.IsWindowEnabledSafe(hwndButton))
            {
                return;
            }

            // Fallback: имитация нажатия (с таймаутами)
            _ = User32.SendMessageSafe(hwndButton, Win32Consts.BmSetState, 1, IntPtr.Zero);
            _ = User32.SendMessageSafe(hwndButton, Win32Consts.WmLButtonDown, IntPtr.Zero, IntPtr.Zero);
            _ = User32.SendMessageSafe(hwndButton, Win32Consts.WmLButtonUp, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            WinApiHelper.LogError(nameof(SendButtonClick), ex, $"hWnd={hwndButton}");
        }
    }

}
