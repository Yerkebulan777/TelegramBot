namespace TelegramBot.Core.Models;

/// <summary>Детальный статус сессии с разбивкой по состояниям команд.</summary>
public class SessionStatus
{
    public string Status { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string? ProjectName { get; set; }
    public DateTime CreatedAt { get; set; }
    public int TotalFiles { get; set; }
    public int DoneFiles { get; set; }
    public int FailedFiles { get; set; }
    public int ProcessingFiles { get; set; }
    public int PendingFiles { get; set; }
}
