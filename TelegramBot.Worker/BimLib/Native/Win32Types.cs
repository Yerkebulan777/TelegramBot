using System.Runtime.InteropServices;

namespace TelegramBot.BimLib.Native;

internal static class Win32Consts
{
    internal const int BmClick = 0x00F5;
    internal const int BmSetState = 0x00F3;
    internal const int WmLButtonDown = 0x0201;
    internal const int WmLButtonUp = 0x0202;
    internal const int WmClose = 0x0010;
    internal const int BnsBusy = 0x0004;
}
