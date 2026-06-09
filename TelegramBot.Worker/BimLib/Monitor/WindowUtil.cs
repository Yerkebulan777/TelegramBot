using System.Text;
using TelegramBot.Worker.BimLib.Native;

namespace TelegramBot.Worker.BimLib.Monitor;

/// <summary>Win32-утилиты: поиск окон, получение информации, клики.</summary>
internal static class WindowUtil
{
    private const int BufferSize = 8193;

    /// <summary>Получает заголовок окна по HWND.</summary>
    internal static string GetWindowTitle(IntPtr hwnd)
    {
        var length = User32.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(length + 1);
        _ = User32.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Получает имя класса окна по HWND.</summary>
    internal static string GetWindowClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(BufferSize);
        _ = User32.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Получает ID процесса, которому принадлежит окно.</summary>
    internal static uint GetWindowProcessId(IntPtr hwnd)
    {
        _ = User32.GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    /// <summary>Получает владельца окна.</summary>
    internal static IntPtr GetOwnerWindow(IntPtr hwnd)
    {
        // GetWindow with GW_OWNER = 4
        return User32.GetWindow(hwnd, 4);
    }

    /// <summary>Получает родительское окно.</summary>
    internal static IntPtr GetParentWindow(IntPtr hwnd)
    {
        return User32.GetParent(hwnd);
    }

    /// <summary>Получает Control ID диалогового элемента.</summary>
    internal static int GetDialogControlId(IntPtr hwnd)
    {
        return User32.GetDlgCtrlID(hwnd).ToInt32();
    }

    /// <summary>
    /// Перечисляет все top-level окна, соответствующие указанным критериям.
    /// </summary>
    internal static List<IntPtr> GetTopLevelWindows(string? className = null, string? windowTitle = null, uint? processId = null)
    {
        var result = new List<IntPtr>();

        _=User32.EnumWindows((hwnd, _) =>
        {
            if (!User32.IsWindowVisible(hwnd))
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
            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>
    /// Перечисляет дочерние окна указанного родителя, соответствующие критериям.
    /// </summary>
    internal static List<IntPtr> EnumerateChildWindows(IntPtr parentHwnd, string? className = null, string? windowTitle = null)
    {
        var result = new List<IntPtr>();

        _=User32.EnumChildWindows(parentHwnd, (hwnd, _) =>
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
            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>Отправляет клик по кнопке (BM_CLICK или fallback).</summary>
    internal static void SendButtonClick(IntPtr hwndButton)
    {
        // Пробуем BM_CLICK
        _ = User32.SendMessage(hwndButton, Win32Consts.BmClick, IntPtr.Zero, IntPtr.Zero);

        // Если кнопка не реагирует — пробуем установить состояние и отправить LBUTTON
        if (!User32.IsWindowEnabled(hwndButton))
        {
            return;
        }

        _ = User32.SendMessage(hwndButton, Win32Consts.BmSetState, 1, IntPtr.Zero);
        _ = User32.SendMessage(hwndButton, Win32Consts.WmLButtonDown, IntPtr.Zero, IntPtr.Zero);
        _ = User32.SendMessage(hwndButton, Win32Consts.WmLButtonUp, IntPtr.Zero, IntPtr.Zero);
    }

}
