namespace TelegramBot.Worker.BimLib.Monitor;

/// <summary>Информация об окне Windows: дескриптор, заголовок, класс, процесс.</summary>
internal sealed class WindowInfo
{
    public IntPtr Hwnd { get; }
    public IntPtr OwnerWindow { get; }
    public IntPtr ParentWindow { get; }
    public int DialogControlId { get; }
    public string WindowClassName { get; }
    public string WindowTitle { get; }
    public uint ProcessId { get; }

    private WindowInfo(IntPtr hwnd, IntPtr ownerWindow, IntPtr parentWindow,
        int dialogControlId, string className, string title, uint processId)
    {
        Hwnd = hwnd;
        OwnerWindow = ownerWindow;
        ParentWindow = parentWindow;
        DialogControlId = dialogControlId;
        WindowClassName = className;
        WindowTitle = title;
        ProcessId = processId;
    }

    /// <summary>Создаёт WindowInfo из HWND, запрашивая все необходимые данные через WinAPI.</summary>
    public static WindowInfo FromHandle(IntPtr hwnd)
    {
        var ownerWindow = WindowUtil.GetOwnerWindow(hwnd);
        var parentWindow = WindowUtil.GetParentWindow(hwnd);
        var dialogControlId = WindowUtil.GetDialogControlId(hwnd);
        var className = WindowUtil.GetWindowClassName(hwnd);
        var title = WindowUtil.GetWindowTitle(hwnd);
        var processId = WindowUtil.GetWindowProcessId(hwnd);

        return new WindowInfo(hwnd, ownerWindow, parentWindow,
            dialogControlId, className, title, processId);
    }

    public override string ToString()
    {
        return $"HWND={Hwnd}, Title='{WindowTitle}', Class='{WindowClassName}', PID={ProcessId}";
    }
}
