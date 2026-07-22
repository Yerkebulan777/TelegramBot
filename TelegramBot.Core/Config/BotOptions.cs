namespace TelegramBot.Core.Config;

public sealed class BotOptions
{
    public const string SectionName = "TelegramBot";
    public string Token { get; set; } = string.Empty;
}
