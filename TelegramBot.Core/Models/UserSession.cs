namespace TelegramBot.Core.Models;

/// <summary>
/// Represents a user session with thread-safe collections and DB-backed message tracking.
/// </summary>
public class UserSession
{
    public long UserId { get; init; }
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;

    public string CurrentPath { get; set; } = Directory.GetCurrentDirectory();


    private readonly object _commandLock = new();
    private readonly List<string> _pendingCommand = [];
    private readonly List<string> _pendingCommandName = [];

    private readonly object _selectionLock = new();
    private readonly HashSet<string> _selectedFiles = [];

    // Public read-only wrappers with thread-safe access
    public IReadOnlyList<string> PendingCommand
    {
        get
        {
            lock (_commandLock)
            {
                return [.. _pendingCommand];
            }
        }
    }

    public IReadOnlyList<string> PendingCommandName
    {
        get
        {
            lock (_commandLock)
            {
                return [.. _pendingCommandName];
            }
        }
    }

    public IReadOnlySet<string> GetSelectedFiles()
    {
        lock (_selectionLock)
        {
            return new HashSet<string>(_selectedFiles);
        }
    }

    /// <summary>True when the user is viewing the top-level sessions list (status view).</summary>
    public bool IsInStatusView { get; set; }
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
        lock (_commandLock)
        {
            _pendingCommand.Add(code);
            _pendingCommandName.Add(displayName);
        }
    }

    public bool RemovePendingCommand(string code)
    {
        lock (_commandLock)
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
    }

    public bool ContainsPendingCommand(string code)
    {
        lock (_commandLock)
        {
            return _pendingCommand.Contains(code);
        }
    }

    public void ClearPendingCommands()
    {
        lock (_commandLock)
        {
            _pendingCommand.Clear();
            _pendingCommandName.Clear();
        }
    }

    // File selection methods
    public bool ToggleSelectedFile(string filePath)
    {
        lock (_selectionLock)
        {
            if (_selectedFiles.Contains(filePath))
            {
                _=_selectedFiles.Remove(filePath);
                return false;
            }
            _=_selectedFiles.Add(filePath);
            return true;
        }
    }

    public void ClearSelectedFiles()
    {
        lock (_selectionLock)
        {
            _selectedFiles.Clear();
        }
    }

    /// <summary>
    /// Resets the session state for a new command flow.
    /// </summary>
    public void Reset(string rootPath)
    {
        ClearSelectedFiles();
        ClearPendingCommands();
        CurrentPath = rootPath;
        IsInStatusView = false;
        CommandSelectionMessageId = null;
        IsFileSelectionActive = false;
        FileSelectionMessageId = null;
        LastActionsMessageId = null;
        StatusMessageId = null;
        SessionId = 0;
        LastUserMessageId = null;
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
