namespace TelegramBot.Core.Models;

/// <summary>
/// Отслеженное сообщение Telegram.
/// </summary>
public sealed class TrackedMessage
{
    public int Id { get; init; }
    public int? SessionId { get; init; }
    public long ChatId { get; init; }
    public int MessageId { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
