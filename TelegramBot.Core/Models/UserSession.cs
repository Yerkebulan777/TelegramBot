using System.Collections.Concurrent;

namespace TelegramBot.Core.Models;

/// <summary>
/// Represents a user session with thread-safe collections and clear state management.
/// </summary>
public class UserSession
{
    public long UserId { get; init; }
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;

    public string CurrentPath { get; set; } = Directory.GetCurrentDirectory();

    // ConcurrentDictionary: token-to-path mapping used by FileSystemBrowser and handlers
    public ConcurrentDictionary<string, string> PathMap { get; } = new();

    private readonly object _commandLock = new();
    private readonly List<string> _pendingCommand = [];
    private readonly List<string> _pendingCommandName = [];

    private readonly object _selectionLock = new();
    private readonly HashSet<string> _selectedFiles = [];

    private readonly object _messageLock = new();
    private readonly List<int> _trackedMessageIds = [];

    // Public read-only wrappers with thread-safe access
    public IReadOnlyList<string> PendingCommand
    {
        get { lock (_commandLock) return [.. _pendingCommand]; }
    }

    public IReadOnlyList<string> PendingCommandName
    {
        get { lock (_commandLock) return [.. _pendingCommandName]; }
    }

    public IReadOnlySet<string> SelectedFiles
    {
        get { lock (_selectionLock) return new HashSet<string>(_selectedFiles); }
    }

    /// <summary>True when the user is viewing the top-level sessions list (status view).</summary>
    public bool IsInStatusView { get; set; }
    public int SessionId { get; set; }
    public int? CommandSelectionMessageId { get; set; }
    public bool IsFileSelectionActive { get; set; }
    public int? FileSelectionMessageId { get; set; }
    public int? StatusMessageId { get; set; }

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
            int index = _pendingCommand.IndexOf(code);
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
        lock (_commandLock) return _pendingCommand.Contains(code);
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
                _selectedFiles.Remove(filePath);
                return false;
            }
            _selectedFiles.Add(filePath);
            return true;
        }
    }

    public void ClearSelectedFiles()
    {
        lock (_selectionLock) _selectedFiles.Clear();
    }

    // Message tracking (user commands + bot responses)
    public void TrackMessage(int messageId)
    {
        lock (_messageLock) _trackedMessageIds.Add(messageId);
    }

    public IReadOnlyList<int> TakeTrackedMessages()
    {
        lock (_messageLock)
        {
            var ids = new List<int>(_trackedMessageIds);
            _trackedMessageIds.Clear();
            return ids;
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
        SessionId = 0;
        CommandSelectionMessageId = null;
        IsFileSelectionActive = false;
        FileSelectionMessageId = null;
        StatusMessageId = null;
        lock (_messageLock) _trackedMessageIds.Clear();
    }

    /// <summary>
    /// Resets navigation state (path, selected files).
    /// Does not affect pending commands.
    /// </summary>
    public void ResetNavigation(string rootPath)
    {
        CurrentPath = rootPath;
        ClearSelectedFiles();
        FileSelectionMessageId = null;
    }
}
