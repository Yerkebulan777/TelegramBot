namespace TelegramBot.Core.Config;

public class RateLimitOptions
{
    public const string SectionName = "RateLimit";
    public int MaxRequests { get; init; } = 10;
    public int WindowSeconds { get; init; } = 60;
    public int MaxFilesPerUserPerDay { get; init; } = 1000;
}
