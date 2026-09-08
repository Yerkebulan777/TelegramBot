namespace TelegramBot.Data.Models;

public sealed class NotificationOutboxItem
{
    public long OutboxId { get; set; }
    public int SessionId { get; set; }

    public required string CorrelationId { get; set; }

    public int Attempts { get; set; }
}
