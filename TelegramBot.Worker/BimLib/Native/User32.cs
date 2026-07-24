using System.Runtime.InteropServices;
using System.Text;
using TelegramBot.BimLib.Helpers;

namespace TelegramBot.BimLib.Native;

internal static class User32
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    internal static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    internal static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowEnabled(IntPtr hWnd);

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // ---------------------------------------------------------------
    // Safe wrappers — each wraps a raw P/Invoke with try-catch,
    // return-value validation, and structured error logging.
    // ---------------------------------------------------------------

    /// <summary>Safe: retrieves window title. Returns <c>null</c> on failure.</summary>
    internal static string? GetWindowTextSafe(IntPtr hWnd)
    {
        try
        {
            var length = GetWindowTextLength(hWnd);
            if (length <= 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 0)
                {
                    WinApiHelper.LogWarning(nameof(GetWindowText), $"hWnd={hWnd}", error);
                }

                return null;
            }

            var sb = new StringBuilder(length + 1);
            var result = GetWindowText(hWnd, sb, sb.Capacity);
            if (result <= 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 0)
                {
                    WinApiHelper.LogWarning(nameof(GetWindowText), $"hWnd={hWnd}", error);
                }

                return null;
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            WinApiHelper.LogError(nameof(GetWindowText), ex, $"hWnd={hWnd}");
            return null;
        }
    }

    /// <summary>Safe: retrieves window class name. Returns <c>null</c> on failure.</summary>
    internal static string? GetClassNameSafe(IntPtr hWnd)
    {
        try
        {
            var sb = new StringBuilder(256);
            var result = GetClassName(hWnd, sb, sb.Capacity);
            if (result <= 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 0)
                {
                    WinApiHelper.LogWarning(nameof(GetClassName), $"hWnd={hWnd}", error);
                }

                return null;
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            WinApiHelper.LogError(nameof(GetClassName), ex, $"hWnd={hWnd}");
            return null;
        }
    }

    /// <summary>Safe: retrieves process/thread ID. Returns 0 on failure.</summary>
    internal static uint GetWindowThreadProcessIdSafe(IntPtr hWnd)
    {
        try
        {
            _ = GetWindowThreadProcessId(hWnd, out var pid);
            return pid;
        }
        catch (Exception ex)
        {
            WinApiHelper.LogError(nameof(GetWindowThreadProcessId), ex, $"hWnd={hWnd}");
            return 0;
        }
    }

    /// <summary>Safe: enumerates top-level windows. Returns <c>false</c> on failure.</summary>
    internal static bool EnumWindowsSafe(EnumWindowsProc lpEnumFunc, IntPtr lParam)
    {
        try
        {
            if (!EnumWindows(lpEnumFunc, lParam))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 0)
                {
                    WinApiHelper.LogWarning(nameof(EnumWindows), $"lParam={lParam}", error);
                }

                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            WinApiHelper.LogError(nameof(EnumWindows), ex, $"lParam={lParam}");
            return false;
        }
    }

    /// <summary>Safe: enumerates child windows. Returns <c>false</c> on failure.</summary>
    internal static bool EnumChildWindowsSafe(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam)
    {
        try
        {
            if (!EnumChildWindows(hWndParent, lpEnumFunc, lParam))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 0)
                {
                    WinApiHelper.LogWarning(nameof(EnumChildWindows), $"parent={hWndParent}, lParam={lParam}", error);
                }

                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            WinApiHelper.LogError(nameof(EnumChildWindows), ex, $"parent={hWndParent}");
            return false;
        }
    }

    /// <summary>
    /// Safe: posts a message asynchronously. Never blocks, even if target window is unresponsive.
    /// Returns <c>true</c> on success, <c>false</c> on failure.
    /// </summary>
    internal static bool PostMessageSafe(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            var result = PostMessage(hWnd, msg, wParam, lParam);
            if (!result)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 0)
                {
                    WinApiHelper.LogWarning(nameof(PostMessage), $"msg=0x{msg:X}, hWnd={hWnd}", error);
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            WinApiHelper.LogError(nameof(PostMessage), ex, $"msg=0x{msg:X}, hWnd={hWnd}");
            return false;
        }
    }

    /// <summary>Safe: retrieves window long value. Returns 0 on failure.</summary>
    internal static int GetWindowLongSafe(IntPtr hWnd, int nIndex)
    {
        try
        {
            return GetWindowLong(hWnd, nIndex);
        }
        catch (Exception ex)
        {
            WinApiHelper.LogError(nameof(GetWindowLong), ex, $"hWnd={hWnd}, index={nIndex}");
            return 0;
        }
    }

    /// <summary>Safe: checks window visibility. Returns <c>false</c> on failure.</summary>
    internal static bool IsWindowVisibleSafe(IntPtr hWnd)
    {
        try
        {
            return IsWindowVisible(hWnd);
        }
        catch (Exception ex)
        {
            WinApiHelper.LogError(nameof(IsWindowVisible), ex, $"hWnd={hWnd}");
            return false;
        }
    }

    /// <summary>Safe: checks window enabled state. Returns <c>false</c> on failure.</summary>
    internal static bool IsWindowEnabledSafe(IntPtr hWnd)
    {
        try
        {
            return IsWindowEnabled(hWnd);
        }
        catch (Exception ex)
        {
            WinApiHelper.LogError(nameof(IsWindowEnabled), ex, $"hWnd={hWnd}");
            return false;
        }
    }
}
