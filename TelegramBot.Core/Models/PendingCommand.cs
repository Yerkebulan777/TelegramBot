namespace TelegramBot.Core.Models;

/// <summary>Команда, ожидающая выполнения воркером.</summary>
public class PendingCommand
{
    public int CommandId { get; set; }
    public int SessionId { get; set; }
    public required string CorrelationId { get; set; }
    public required string CommandText { get; set; }
    public string? FilePath { get; set; }
    public string? RootPath { get; set; }
    public long UserId { get; set; }
    public int RetryCount { get; set; }

    /// <summary>Unix-секунды lease, выставленные при claim. Нужны для fencing retry/complete/shutdown.</summary>
    public long Lease { get; set; }
}
