namespace TelegramBot.Core.Models;

/// <summary>Сводка завершённой сессии для Telegram-уведомления.</summary>
public sealed class SessionCompletionSummary
{
    public long UserId { get; set; }
    public string? Username { get; set; }
    public int SessionId { get; set; }
    public string? ProjectName { get; set; }
    public int DoneFiles { get; set; }
    public int FailedFiles { get; set; }
    public int TotalFiles { get; set; }
    public int? DurationSeconds { get; set; }
    public List<FailedCommandInfo> FailedCommands { get; set; } = [];
    public List<FailedCommandInfo> WarnedCommands { get; set; } = [];
}

/// <summary>Упавшая команда сессии: путь файла и причина сбоя для Telegram-уведомления.</summary>
public sealed class FailedCommandInfo
{
    public string FilePath { get; set; } = string.Empty;
    public string? RootPath { get; set; }
    public string CommandText { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}
