namespace TelegramBot.Server.Services.Infrastructure.Telegram;

public sealed record NotificationItem(
    int SessionId,
    string CorrelationId);
