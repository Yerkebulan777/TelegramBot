using TelegramBotServer.Models;

namespace TelegramBotServer.Interfaces;

public interface IDataService
{
    /// <summary>
    /// Initializes the database schema and seed data.
    /// </summary>
    Task InitializeDatabaseAsync();

    /// <summary>
    /// Updates the status of a command (e.g. pending → in_progress → done).
    /// </summary>
    Task UpdateCommandStatusAsync(int commandId, string status);

    /// <summary>
    /// Checks if a user is in the whitelist.
    /// </summary>
    Task<bool> IsUserAuthorizedAsync(long userId);

    /// <summary>
    /// Adds a user to the authorization whitelist.
    /// </summary>
    Task AddAuthorizedUserAsync(long userId, string username);

    /// <summary>
    /// Validates a password against stored credentials.
    /// </summary>
    Task<bool> ValidatePasswordAsync(string password);

    /// <summary>
    /// Gets all commands for a user (excluding deleted).
    /// </summary>
    Task<List<Command>> GetUserCommandsAsync(long userId);

    /// <summary>
    /// Creates a new session with associated commands in a single transaction.
    /// </summary>
    Task<long> CreateSessionWithCommandsAsync(
        IEnumerable<string> commandText,
        IEnumerable<string> files,
        long userId,
        string username,
        int priorityId,
        int filesAmount);

    /// <summary>
    /// Gets the list of sessions for a user (excluding deleted).
    /// </summary>
    Task<List<SessionsList>> GetSessionsListAsync(long userId);

    /// <summary>
    /// Gets the status summary for a user-owned session (total files, done files).
    /// </summary>
    Task<SessionStatus> GetSessionsStatusAsync(int sessionId, long userId);

    /// <summary>
    /// Gets all commands within a user-owned session (excluding deleted).
    /// </summary>
    Task<List<SessionCommands>> GetSessionsCommandsAsync(int sessionId, long userId);

    /// <summary>
    /// Soft-deletes a user-owned session and all its commands.
    /// </summary>
    Task<bool> DeleteSessionAsync(int sessionId, long userId);

    /// <summary>
    /// Soft-deletes a single user-owned command.
    /// </summary>
    Task<bool> DeleteCommandAsync(int commandId, long userId);

    /// <summary>
    /// Checks if a user-owned session has any non-deleted commands remaining.
    /// </summary>
    Task<bool> CheckCommandsStatusAsync(int sessionId, long userId);

    /// <summary>
    /// Resolves the session ID for a user-owned command.
    /// </summary>
    Task<int?> GetSessionIdByCommandAsync(int commandId, long userId);
}
