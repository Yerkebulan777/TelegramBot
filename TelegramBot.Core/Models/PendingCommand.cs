namespace TelegramBot.Core.Models;

/// <summary>Команда, ожидающая выполнения воркером.</summary>
public class PendingCommand
{
    public int CommandId { get; set; }
    public int SessionId { get; set; }
    public required string CorrelationId { get; set; }
    public required string CommandText { get; set; }
    public string? FilePath { get; set; }
    public long UserId { get; set; }
    /// <summary>
    /// Partition из БД. Само partition-scheduling («одна команда на Partition за раз»)
    /// выполняется целиком в SQL (ClaimAndReturn: DISTINCT ON Partition + advisory lock),
    /// поэтому в C# не читается — оставлено в модели как документация контракта.
    /// </summary>
    public string? Partition { get; set; }
    public int RetryCount { get; set; }
}
