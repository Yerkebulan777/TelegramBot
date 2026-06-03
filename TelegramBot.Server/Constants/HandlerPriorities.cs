namespace TelegramBot.Server.Constants;

internal static class HandlerPriorities
{
    internal const int AccessRequest  = 0;
    internal const int FileNavigation = 10;
    internal const int FileSelection  = 20;
    internal const int Default        = 100;
}
