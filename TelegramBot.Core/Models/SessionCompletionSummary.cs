namespace TelegramBot.Core.Models;

/// <summary>Сводка завершённой сессии для Telegram-уведомления.</summary>
public sealed class SessionCompletionSummary
{
    public long UserId { get; set; }
    public string? Username { get; set; }
    public int SessionId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string? ProjectName { get; set; }
    public int DoneFiles { get; set; }
    public int FailedFiles { get; set; }
    public int TotalFiles { get; set; }
    public int? DurationSeconds { get; set; }
    public List<string> FailedFilePaths { get; set; } = [];
}
