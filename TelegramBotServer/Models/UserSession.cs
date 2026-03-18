using System.Collections.Concurrent;

namespace TelegramBotServer.Models;

/// <summary>
/// Represents a user session with thread-safe collections and clear state management.
/// </summary>
public class UserSession
{
    public long UserId { get; init; }
    public SessionState State { get; set; } = SessionState.Idle;
    public string? TempData { get; set; }
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;

    public string CurrentPath { get; set; } = Directory.GetCurrentDirectory();

    // ConcurrentDictionary: token-to-path mapping used by FileSystemBrowser and handlers
    public ConcurrentDictionary<string, string> PathMap { get; } = new();

    // Thread-safe collections for concurrent access
    private readonly object _commandLock = new();
    private readonly List<string> _pendingCommand = [];
    private readonly List<string> _pendingCommandName = [];

    private readonly object _selectionLock = new();
    private readonly HashSet<string> _selectedFiles = [];

    private readonly object _navigationLock = new();
    private readonly List<int> _pagesCache = [];
    private readonly List<FileSystemItem> _items = [];

    private readonly object _messageLock = new();
    private readonly List<int> _botMessageIds = [];

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

    public IReadOnlyList<int> PagesCache
    {
        get { lock (_navigationLock) return [.. _pagesCache]; }
    }

    public IReadOnlyList<FileSystemItem> Items
    {
        get { lock (_navigationLock) return [.. _items]; }
    }

    public SelectionMode SelectionType { get; set; } = SelectionMode.Files;
    public string? RootPath { get; set; }
    /// <summary>True when the user is navigating into a subfolder level.</summary>
    public bool IsNavigatingDeep { get; set; }
    public int Counter { get; set; }
    /// <summary>True when the user is viewing the top-level sessions list (status view).</summary>
    public bool IsInStatusView { get; set; }
    public int SessionId { get; set; }
    public bool IsFileSelectionActive { get; set; }
    public int? FileSelectionMessageId { get; set; }

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

    public void SetSelectedFiles(IEnumerable<string> files)
    {
        lock (_selectionLock)
        {
            _selectedFiles.Clear();
            foreach (var file in files)
                _selectedFiles.Add(file);
        }
    }

    public void ClearSelectedFiles()
    {
        lock (_selectionLock) _selectedFiles.Clear();
    }

    // Navigation methods
    public void AddToPagesCache(int page)
    {
        lock (_navigationLock) _pagesCache.Add(page);
    }

    public void RemoveLastFromPagesCache()
    {
        lock (_navigationLock)
        {
            if (_pagesCache.Count > 0)
                _pagesCache.RemoveAt(_pagesCache.Count - 1);
        }
    }

    public int GetLastPageFromCache()
    {
        lock (_navigationLock) return _pagesCache.Count > 0 ? _pagesCache[^1] : 0;
    }

    public void ClearPagesCache()
    {
        lock (_navigationLock) _pagesCache.Clear();
    }

    public void SetItems(IEnumerable<FileSystemItem> newItems)
    {
        lock (_navigationLock)
        {
            _items.Clear();
            _items.AddRange(newItems);
        }
    }

    public void ClearItems()
    {
        lock (_navigationLock) _items.Clear();
    }

    // Bot message tracking methods
    public void AddBotMessageId(int messageId)
    {
        lock (_messageLock) _botMessageIds.Add(messageId);
    }

    public IReadOnlyList<int> TakeAllBotMessageIds()
    {
        lock (_messageLock)
        {
            var ids = new List<int>(_botMessageIds);
            _botMessageIds.Clear();
            return ids;
        }
    }

    /// <summary>
    /// Resets the session state for a new command flow.
    /// </summary>
    public void Reset(string rootPath)
    {
        ClearPagesCache();
        ClearSelectedFiles();
        ClearPendingCommands();
        CurrentPath = rootPath;
        Counter = 0;
        ClearItems();
        IsFileSelectionActive = false;
        FileSelectionMessageId = null;
        lock (_messageLock) _botMessageIds.Clear();
    }

    /// <summary>
    /// Resets navigation state (path, level, counter, selected files, pages cache, items).
    /// Does not affect pending commands or SelectionType.
    /// </summary>
    public void ResetNavigation(string rootPath)
    {
        CurrentPath = rootPath;
        IsNavigatingDeep = false;
        Counter = 0;
        ClearSelectedFiles();
        ClearPagesCache();
        ClearItems();
        FileSelectionMessageId = null;
    }
}

/// <summary>
/// Represents the current state of a user session.
/// </summary>
public enum SessionState
{
    Idle
}
