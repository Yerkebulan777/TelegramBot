using TelegramBotServer.Models;

namespace TelegramBotServer.Interfaces;

public interface IDataService
{
    // ?? Queue management
    Task AddCommandAsync(long userId, List<string> fileList, List<string> commandList);
    Task<Command?> GetNextCommandAsync();
    Task UpdateCommandStatusAsync(int commandId, string status);

    // ?? User management
    Task<bool> IsUserAuthorizedAsync(long userId);
    Task AddAuthorizedUserAsync(long userId, string username);//DateTime timestamp

    Task<bool> ValidatePasswordAsync(string password);

    // ?? Debug / Logging
    Task<List<Command>> GetUserCommandsAsync(long userId);

    Task<bool> RemoveCommandFromQueue(int id);

    Task<long> CreateSessionWithCommandsAsync(
        IEnumerable<string> commandText,
        IEnumerable<string> files,
        long userId,
        string username,
        int priorityId,
        int filesAmount);


    Task<List<SessionsList>> GetSessionsListAsync(long userId);

    Task<SessionStatus> GetSessionsStatusAsync(int sessionId);

    Task<List<SessionCommands>> GetSessionsCommandsAsync(int sessionId);

    Task<bool> DeleteSessionAsync(int sessionId);


    Task<bool> DeleteCommandAsync(int commandId);

    Task<bool> CheckCommandsStatusAsync(int sessionId);
    Task InitializeDatabaseAsync();
}