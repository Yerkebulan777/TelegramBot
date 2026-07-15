using TelegramBot.BimLib.Native;

namespace TelegramBot.BimLib.Models;

/// <summary>Информация об окне Windows: дескриптор, заголовок, класс, процесс.</summary>
internal sealed class WindowInfo
{
    public IntPtr Hwnd { get; }
    public string WindowClassName { get; }
    public string WindowTitle { get; }
    public uint ProcessId { get; }

    private WindowInfo(IntPtr hwnd, string className, string title, uint processId)
    {
        Hwnd = hwnd;
        WindowClassName = className;
        WindowTitle = title;
        ProcessId = processId;
    }

    /// <summary>Создаёт WindowInfo из HWND, запрашивая все необходимые данные через WinAPI.</summary>
    public static WindowInfo FromHandle(IntPtr hwnd)
    {
        var className = WindowUtil.GetWindowClassName(hwnd);
        var title = WindowUtil.GetWindowTitle(hwnd);
        var processId = WindowUtil.GetWindowProcessId(hwnd);

        return new WindowInfo(hwnd, className, title, processId);
    }

    public override string ToString()
    {
        return $"HWND={Hwnd}, Title='{WindowTitle}', Class='{WindowClassName}', PID={ProcessId}";
    }
}
