namespace TelegramBot.Server.Services.Infrastructure.Telegram;

public sealed record NotificationItem(
    long UserId,
    int SessionId,
    int Done,
    int Total,
    string? ProjectName);
