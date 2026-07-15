namespace TelegramBot.BimLib.Native;

internal static class Win32Consts
{
    // Button messages
    internal const int BmClick = 0x00F5;
    internal const int BmSetState = 0x00F3;

    // Mouse messages
    internal const int WmLButtonDown = 0x0201;
    internal const int WmLButtonUp = 0x0202;

    // Window messages
    internal const int WmClose = 0x0010;
    internal const int WmCommand = 0x0111;
    internal const int WmSysCommand = 0x0112;

    // WM_COMMAND notification codes
    internal const int BnClicked = 0; // HIWORD(wParam) = BN_CLICKED

    // WM_SYSCOMMAND params
    internal const int ScClose = 0xF060;

    // GetWindowLong index
    internal const int GWL_ID = -12;
}
