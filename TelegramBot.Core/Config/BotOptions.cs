namespace TelegramBot.Core.Config;

public sealed class BotOptions
{
    public const string SectionName = "TelegramBot";
    public long[] AdminUserIds { get; set; } = [];
}
