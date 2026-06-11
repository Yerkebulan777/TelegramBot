namespace TelegramBot.Core.Models;

/// <summary>Команда, ожидающая выполнения воркером.</summary>
public class PendingCommand
{
    public int CommandId { get; set; }
    public int SessionId { get; set; }
    public required string CorrelationId { get; set; }
    public required string CommandText { get; set; }
    public string? FilePath { get; set; }
    public int ExecutionOrder { get; set; }
    public long UserId { get; set; }
    public string? Username { get; set; }
    public string? Partition { get; set; }
    public int Priority { get; set; }
    public int RetryCount { get; set; }
}
