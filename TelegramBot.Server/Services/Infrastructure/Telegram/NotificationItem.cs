namespace TelegramBot.Server.Services.Infrastructure.Telegram;

// UserId non-null = session started; null = session completed
public sealed record NotificationItem(
    int SessionId,
    string CorrelationId,
    long? UserId = null);
