namespace TelegramBot.Server.Services.Infrastructure.Telegram;

public sealed record NotificationItem(
    int? SessionId,
    string? CorrelationId,
    long? UserId = null,
    bool DrainCompletionOutbox = false)
{
    public static NotificationItem CompletionWakeUp { get; } = new(null, null, DrainCompletionOutbox: true);

    public static NotificationItem SessionStarted(int sessionId, string correlationId, long userId)
    {
        return new(sessionId, correlationId, userId);
    }
}
