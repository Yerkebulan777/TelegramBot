using TelegramBotServer.Models;

namespace TelegramBotServer.Interfaces;

public interface IDataService
{
    /// <summary>Инициализирует базу данных (создает таблицы).</summary>
    Task InitializeDatabaseAsync();

    /// <summary>Обновляет статус команды.</summary>
    Task UpdateCommandStatusAsync(int commandId, string status);

    /// <summary>Возвращает все команды пользователя.</summary>
    Task<List<Command>> GetUserCommandsAsync(long userId);

    /// <summary>Создает новую сессию с командами.</summary>
    Task<long> CreateSessionWithCommandsAsync(
        IEnumerable<string> commandText,
        IEnumerable<string> files,
        long userId,
        string username,
        int priorityId,
        int filesAmount);

    /// <summary>Возвращает список сессий пользователя.</summary>
    Task<List<SessionsList>> GetSessionsListAsync(long userId);

    /// <summary>Возвращает статус сессии.</summary>
    Task<SessionStatus> GetSessionsStatusAsync(int sessionId, long userId);

    /// <summary>Возвращает список команд в сессии.</summary>
    Task<List<SessionCommands>> GetSessionsCommandsAsync(int sessionId, long userId);

    /// <summary>Удаляет сессию (мягкое удаление).</summary>
    Task<bool> DeleteSessionAsync(int sessionId, long userId);

    /// <summary>Удаляет команду (мягкое удаление).</summary>
    Task<bool> DeleteCommandAsync(int commandId, long userId);

    /// <summary>Проверяет наличие команд в сессии.</summary>
    Task<bool> CheckCommandsStatusAsync(int sessionId, long userId);

    /// <summary>Возвращает ID сессии по ID команды.</summary>
    Task<int?> GetSessionIdByCommandAsync(int commandId, long userId);
}
