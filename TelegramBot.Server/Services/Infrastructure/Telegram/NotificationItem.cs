namespace TelegramBot.Server.Services.Infrastructure.Telegram;

public sealed record NotificationItem(
    long UserId,
    int SessionId,
    string CorrelationId,
    int Done,
    int Total,
    string? ProjectName);
