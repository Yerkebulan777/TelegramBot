using TelegramBot.Core.Constants;

namespace TelegramBot.Core.Models;

/// <summary>Сводка завершённой сессии для Telegram-уведомления.</summary>
public sealed class SessionCompletionSummary
{
    public long UserId { get; set; }
    public string? Username { get; set; }
    public string? ProjectName { get; set; }
    public int DoneFiles { get; set; }
    public int FailedFiles { get; set; }
    public int TotalFiles { get; set; }
    public int? DurationSeconds { get; set; }
    public List<SessionCommandInfo> Commands { get; set; } = [];

    public IEnumerable<SessionCommandInfo> Failed =>
        Commands.Where(command => string.Equals(command.Status, Statuses.Failed, StringComparison.Ordinal));

    /// <summary>
    /// Done-строки с <see cref="SessionCommandInfo.ErrorMessage"/> — warning плагина, не сбой.
    /// </summary>
    public IEnumerable<SessionCommandInfo> Warned =>
        Commands.Where(command => string.Equals(command.Status, Statuses.Done, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(command.ErrorMessage));
}

/// <summary>Строка команды сессии: код, путь файла, статус и сообщение исполнителя.</summary>
public sealed class SessionCommandInfo
{
    public string FilePath { get; set; } = string.Empty;
    public string? RootPath { get; set; }
    public string CommandText { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}
