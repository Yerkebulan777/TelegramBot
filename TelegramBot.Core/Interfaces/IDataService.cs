using TelegramBot.Core.Models;

namespace TelegramBot.Core.Interfaces;

/// <summary>
/// Persistence service for sessions, commands, and users.
/// </summary>
public interface IDataService
{
    /// <summary>Возвращает запись пользователя или null.</summary>
    Task<BotUser?> GetUserAsync(long userId);

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

    /// <summary>
    /// Атомарно захватывает команды со статусом 'pending' для выполнения воркером.
    /// Использует SELECT ... FOR UPDATE SKIP LOCKED для защиты от конкурентного доступа.
    /// После захвата статус меняется на 'processing' и устанавливается Lease (TTL).
    /// </summary>
    /// <remarks>
    /// TODO (ОБЯЗАТЕЛЬНО): Добавить поддержку партиций — выборка должна учитывать
    /// балансировку между партициями, а не только глобальный приоритет.
    /// </remarks>
    Task<IReadOnlyList<PendingCommand>> ClaimPendingCommandsAsync(int limit = 50);

    /// <summary>Освобождает команды с истёкшим Lease (crash worker recovery).</summary>
    Task ReleaseExpiredLeasesAsync();

    /// <summary>Обновляет статус команды (done, failed, pending).</summary>
    Task<bool> UpdateCommandStatusAsync(int commandId, string status, int? processId = null, string? errorMessage = null);

    /// <summary>Освобождает команды с истёкшим таймаутом выполнения.</summary>
    Task ReleaseTimeoutCommandsAsync(int timeoutSeconds);

    /// <summary>Уведомляет Worker-ов о новых командах через Postgres LISTEN/NOTIFY.</summary>
    Task NotifyNewCommandsAsync(int sessionId);

    /// <summary>Сохраняет ID сообщений для отложенной очистки (прерванный диалог).</summary>
    Task SaveTrackedMessagesAsync(long userId, IEnumerable<int> messageIds);

    /// <summary>Возвращает все сохранённые ID сообщений, сгруппированные по userId.</summary>
    Task<ILookup<long, int>> GetAllTrackedMessagesAsync();

    /// <summary>Удаляет все сохранённые ID сообщений для пользователя.</summary>
    Task DeleteTrackedMessagesAsync(long userId);
}
