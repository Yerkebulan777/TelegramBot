namespace TelegramBot.Core.Constants;

/// <summary>
/// Priority levels for worker command ordering. Smaller values are processed first.
/// </summary>
public static class CommandPriorities
{
    public const int Critical = 1;
    public const int High = 2;
    public const int Medium = 3;
    public const int Low = 4;
    public const int Default = 5;
}
