using TelegramBot.Core.Models;

namespace TelegramBot.Core.Interfaces;

/// <summary>
/// Persistence service for sessions, commands, and users.
/// </summary>
public interface IDataService
{
    /// <summary>Возвращает запись пользователя или null.</summary>
    Task<BotUser?> GetUserAsync(long userId);

    /// <summary>Возвращает запись пользователя по ID (алиас для GetUserAsync).</summary>
    Task<BotUser?> GetBotUserAsync(long userId);

    /// <summary>Создаёт или обновляет запись пользователя (upsert по UserId).</summary>
    Task UpsertUserAsync(BotUser user);

    /// <summary>Инициализирует базу данных (создает таблицы).</summary>
    Task InitializeDatabaseAsync();

    /// <summary>Создает новую сессию с командами.</summary>
    Task<long> CreateSessionWithCommandsAsync(
        IEnumerable<string> commandText,
        IEnumerable<string> files,
        long userId,
        string username,
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

    /// <summary>Создает запрос на доступ для пользователя (статус Pending).</summary>
    Task CreateAccessRequestAsync(long userId, string? username, string? firstName, string? lastName);

    /// <summary>Проверяет, одобрен ли пользователь.</summary>
    Task<bool> IsUserApprovedAsync(long userId);

    /// <summary>Одобряет доступ пользователю.</summary>
    Task<bool> ApproveUserAsync(long userId, long approvedBy);

    /// <summary>Гарантирует наличие администратора в БД.</summary>
    Task EnsureAdminUserAsync(long userId, string? username);

    /// <summary>Сохраняет ID сообщений для отложенной очистки (прерванный диалог).</summary>
    Task SaveTrackedMessagesAsync(long userId, IEnumerable<int> messageIds);

    /// <summary>Возвращает все сохранённые ID сообщений, сгруппированные по userId.</summary>
    Task<ILookup<long, int>> GetAllTrackedMessagesAsync();

    /// <summary>Удаляет все сохранённые ID сообщений для пользователя.</summary>
    Task DeleteTrackedMessagesAsync(long userId);
}
