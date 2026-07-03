namespace TelegramBot.Core.Models;

/// <summary>
/// Represents a user session with DB-backed message tracking.
/// </summary>
public class UserSession
{
    public long UserId { get; init; }
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;

    public string CurrentPath { get; set; } = Directory.GetCurrentDirectory();


    private readonly List<string> _pendingCommand = [];
    private readonly List<string> _pendingCommandName = [];
    private readonly HashSet<string> _selectedFiles = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> PendingCommand => _pendingCommand;

    public IReadOnlyList<string> PendingCommandName => _pendingCommandName;

    public IReadOnlySet<string> GetSelectedFiles()
    {
        return _selectedFiles;
    }

    /// <summary>Текущий фильтр в /status: ALL, ACTIVE, DONE, FAILED.</summary>
    public string StatusFilter { get; set; } = "ALL";

    /// <summary>Текущая страница в /status (0-based). Сбрасывается при смене фильтра.</summary>
    public int StatusPage { get; set; }

    /// <summary>False after server restart — set to true on first slash command to suppress stale-message detection.</summary>
    public bool Initialized { get; set; }
    public int SessionId { get; set; }
    public int? CommandSelectionMessageId { get; set; }
    public bool IsFileSelectionActive { get; set; }
    public int? FileSelectionMessageId { get; set; }
    public int? StatusMessageId { get; set; }
    public int? LastActionsMessageId { get; set; }

    /// <summary>Message ID of the last user-sent message (slash command or reply keyboard button).</summary>
    public int? LastUserMessageId { get; set; }

    // Command manipulation methods
    public void AddPendingCommand(string code, string displayName)
    {
        _pendingCommand.Add(code);
        _pendingCommandName.Add(displayName);
    }

    public bool RemovePendingCommand(string code)
    {
        var index = _pendingCommand.IndexOf(code);
        if (index >= 0)
        {
            _pendingCommand.RemoveAt(index);
            _pendingCommandName.RemoveAt(index);
            return true;
        }

        return false;
    }

    public bool ContainsPendingCommand(string code)
    {
        return _pendingCommand.Contains(code);
    }

    public void ClearPendingCommands()
    {
        _pendingCommand.Clear();
        _pendingCommandName.Clear();
    }

    // File selection methods
    public void AddSelectedFiles(IEnumerable<string> filePaths)
    {
        foreach (var filePath in filePaths)
        {
            _ = _selectedFiles.Add(filePath);
        }
    }

    public bool ToggleSelectedFile(string filePath)
    {
        if (_selectedFiles.Contains(filePath))
        {
            _=_selectedFiles.Remove(filePath);
            return false;
        }

        _=_selectedFiles.Add(filePath);
        return true;
    }

    public void ClearSelectedFiles()
    {
        _selectedFiles.Clear();
    }

    /// <summary>
    /// Resets the session state for a new command flow.
    /// </summary>
    public void Reset(string rootPath)
    {
        ClearSelectedFiles();
        ClearPendingCommands();
        CurrentPath = rootPath;
        CommandSelectionMessageId = null;
        IsFileSelectionActive = false;
        FileSelectionMessageId = null;
        LastActionsMessageId = null;
        StatusMessageId = null;
        SessionId = 0;
        LastUserMessageId = null;
        StatusFilter = "ALL";
        StatusPage = 0;
    }

    /// <summary>
    /// Resets navigation state (path, selected files).
    /// Does not affect pending commands.
    /// </summary>
    public void ResetNavigation(string rootPath)
    {
        ClearSelectedFiles();
        CurrentPath = rootPath;
        FileSelectionMessageId = null;
    }
}
