using System.Runtime.InteropServices;
using System.Text;

namespace TelegramBot.Worker.BimLib.Native;

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
    internal static extern IntPtr GetDlgCtrlID(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetParent(IntPtr hWnd);

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

    /// <summary>Safe: retrieves window title length. Returns 0 on failure.</summary>
    internal static int GetWindowTextLengthSafe(IntPtr hWnd)
    {
        try
        {
            return GetWindowTextLength(hWnd);
        }
        catch (Exception ex)
        {
            WinApiHelper.LogError(nameof(GetWindowTextLength), ex, $"hWnd={hWnd}");
            return 0;
        }
    }

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

    /// <summary>Safe: retrieves dialog control ID. Returns 0 on failure.</summary>
    internal static int GetDlgCtrlIDSafe(IntPtr hWnd)
    {
        try
        {
            var result = GetDlgCtrlID(hWnd);
            if (result == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 0)
                {
                    WinApiHelper.LogWarning(nameof(GetDlgCtrlID), $"hWnd={hWnd}", error);
                }
            }

            return result.ToInt32();
        }
        catch (Exception ex)
        {
            WinApiHelper.LogError(nameof(GetDlgCtrlID), ex, $"hWnd={hWnd}");
            return 0;
        }
    }

    /// <summary>
    /// Safe: sends a window message with timeout protection.
    /// Runs on a background thread to prevent hanging if the target window is unresponsive.
    /// Returns <see cref="IntPtr.Zero"/> on failure or timeout.
    /// </summary>
    internal static IntPtr SendMessageSafe(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        return WinApiHelper.RunWithTimeout(
            () =>
            {
                try
                {
                    var result = SendMessage(hWnd, msg, wParam, lParam);
                    if (result == IntPtr.Zero)
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error != 0)
                        {
                            WinApiHelper.LogWarning(nameof(SendMessage), $"msg=0x{msg:X}, hWnd={hWnd}", error);
                        }
                    }

                    return result;
                }
                catch (Exception ex)
                {
                    WinApiHelper.LogError(nameof(SendMessage), ex, $"msg=0x{msg:X}, hWnd={hWnd}");
                    return IntPtr.Zero;
                }
            },
            $"SendMessage(0x{msg:X})",
            IntPtr.Zero);
    }

    /// <summary>Safe: retrieves a related window handle. Returns <see cref="IntPtr.Zero"/> on failure.</summary>
    internal static IntPtr GetWindowSafe(IntPtr hWnd, int uCmd)
    {
        try
        {
            var result = GetWindow(hWnd, uCmd);
            if (result == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 0)
                {
                    WinApiHelper.LogWarning(nameof(GetWindow), $"hWnd={hWnd}, cmd={uCmd}", error);
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            WinApiHelper.LogError(nameof(GetWindow), ex, $"hWnd={hWnd}, cmd={uCmd}");
            return IntPtr.Zero;
        }
    }

    /// <summary>Safe: retrieves parent window handle. Returns <see cref="IntPtr.Zero"/> on failure.</summary>
    internal static IntPtr GetParentSafe(IntPtr hWnd)
    {
        try
        {
            var result = GetParent(hWnd);
            if (result == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 0)
                {
                    WinApiHelper.LogWarning(nameof(GetParent), $"hWnd={hWnd}", error);
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            WinApiHelper.LogError(nameof(GetParent), ex, $"hWnd={hWnd}");
            return IntPtr.Zero;
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
