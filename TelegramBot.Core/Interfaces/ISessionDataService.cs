using TelegramBot.Core.Models;

namespace TelegramBot.Core.Interfaces;

/// <summary>
/// Service for session data persistence.
/// </summary>
public interface ISessionDataService
{
    /// <summary>Создаёт сессию с командами в одной транзакции.</summary>
    Task<long> CreateSessionWithCommandsAsync(
        IEnumerable<string> commandText,
        IEnumerable<string> files,
        long userId,
        string username,
        int filesAmount,
        string? projectName = null,
        IEnumerable<int>? commandPriorities = null);

    /// <summary>Возвращает список всех сессий.</summary>
    Task<List<SessionsList>> GetSessionsListAsync();

    /// <summary>Возвращает статус сессии.</summary>
    Task<SessionStatus> GetSessionsStatusAsync(int sessionId);

    /// <summary>Возвращает команды сессии.</summary>
    Task<List<SessionCommands>> GetSessionsCommandsAsync(int sessionId);

    /// <summary>Мягкое удаление сессии.</summary>
    Task<bool> DeleteSessionAsync(int sessionId, long userId, bool isAdmin = false);

    /// <summary>Проверяет, есть ли активные команды в сессии.</summary>
    Task<bool> CheckCommandsStatusAsync(int sessionId);

    /// <summary>Считает pending/processing команды в сессии.</summary>
    Task<int> CountPendingProcessingBySessionAsync(int sessionId);

    /// <summary>Возвращает SessionId по CommandId.</summary>
    Task<int?> GetSessionIdByCommandAsync(int commandId, long userId, bool isAdmin = false);

    /// <summary>Мягкое удаление неактивных сессий старше cutoff.</summary>
    Task<int> SoftDeleteInactiveSessionsOlderThanAsync(DateTime cutoffUtc);

    /// <summary>Считает очередь файлов пользователя с момента since.</summary>
    Task<int> CountQueuedFilesByUserSinceAsync(long userId, DateTime sinceUtc);
}
