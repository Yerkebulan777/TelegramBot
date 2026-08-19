namespace TelegramBot.Core.Models;

/// <summary>
/// Represents a user session with DB-backed message tracking.
/// </summary>
public class UserSession
{
    public long UserId { get; init; }
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;

    /// <summary>Состояние и переходы потока выбора задания (команды, файлы, навигация).</summary>
    public SelectionFlow Selection { get; } = new();

    /// <summary>Текущий фильтр в /status: ALL, ACTIVE, DONE, FAILED.</summary>
    public string StatusFilter { get; set; } = "ALL";

    /// <summary>Текущая страница в /status (0-based). Сбрасывается при смене фильтра.</summary>
    public int StatusPage { get; set; }

    /// <summary>False until the first user interaction on this session (slash command, text message, or callback).
    /// Used to detect stale sessions (after restart, idle eviction, or first run) and redirect to /start once.</summary>
    public bool Initialized { get; set; }
    public int SessionId { get; set; }
    public int? CommandSelectionMessageId { get; set; }
    public int? FileSelectionMessageId { get; set; }
    public int? StatusMessageId { get; set; }
    public int? LastActionsMessageId { get; set; }

    /// <summary>Message ID of the last user-sent message (slash command or reply keyboard button).</summary>
    public int? LastUserMessageId { get; set; }

    /// <summary>
    /// Resets the session state for a new command flow. Сброс потока выбора делегируется
    /// в <see cref="SelectionFlow.Reset"/>.
    /// </summary>
    public void Reset(string rootPath)
    {
        Selection.Reset(rootPath);
        CommandSelectionMessageId = null;
        FileSelectionMessageId = null;
        LastActionsMessageId = null;
        StatusMessageId = null;
        SessionId = 0;
        LastUserMessageId = null;
        StatusFilter = "ALL";
        StatusPage = 0;
    }
}
