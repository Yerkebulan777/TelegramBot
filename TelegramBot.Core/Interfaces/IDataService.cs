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
    /// <param name="commandPriorities">Приоритеты для каждой команды (в том же порядке, что commandText). Если null — всем CommandPriorities.Default.</param>
    Task<long> CreateSessionWithCommandsAsync(
        IEnumerable<string> commandText,
        IEnumerable<string> files,
        long userId,
        string username,
        int filesAmount,
        string? projectName = null,
        IEnumerable<int>? commandPriorities = null);

    /// <summary>Возвращает список всех сессий (глобальный статус).</summary>
    Task<List<SessionsList>> GetSessionsListAsync();

    /// <summary>Считает количество файлов, поставленных пользователем в очередь начиная с указанного времени.</summary>
    Task<int> CountQueuedFilesByUserSinceAsync(long userId, DateTime sinceUtc);

    /// <summary>Возвращает статус сессии.</summary>
    Task<SessionStatus> GetSessionsStatusAsync(int sessionId);

    /// <summary>Возвращает список команд в сессии.</summary>
    Task<List<SessionCommands>> GetSessionsCommandsAsync(int sessionId);

    /// <summary>Удаляет сессию (мягкое удаление). Любой одобренный пользователь может удалить любую сессию.</summary>
    Task<bool> DeleteSessionAsync(int sessionId, long userId, bool isAdmin = false);

    /// <summary>Удаляет команду (мягкое удаление). Любой одобренный пользователь может удалить любую команду.</summary>
    Task<bool> DeleteCommandAsync(int commandId, long userId, bool isAdmin = false);

    /// <summary>Проверяет наличие активных команд в сессии.</summary>
    Task<bool> CheckCommandsStatusAsync(int sessionId);

    /// <summary>Считает команды сессии, которые ещё не завершены (pending или processing).</summary>
    Task<int> CountPendingProcessingBySessionAsync(int sessionId);

    /// <summary>Возвращает ID сессии по ID команды. Любой одобренный пользователь может запрашивать любую команду.</summary>
    Task<int?> GetSessionIdByCommandAsync(int commandId, long userId, bool isAdmin = false);

    /// <summary>
    /// Атомарно захватывает команды со статусом 'pending' для выполнения воркером.
    /// Использует SELECT ... FOR UPDATE SKIP LOCKED для защиты от конкурентного доступа.
    /// После захвата статус меняется на 'processing' и устанавливается Lease (TTL).
    /// Пропускает команды, у которых NextRetryAt > NOW().
    /// </summary>
    Task<IReadOnlyList<PendingCommand>> ClaimPendingCommandsAsync(int limit = 50, int leaseTimeoutMinutes = 5);

    /// <summary>Освобождает команды с истёкшим Lease (crash worker recovery).</summary>
    Task ReleaseExpiredLeasesAsync();

    /// <summary>Обновляет статус команды (done, failed, pending).</summary>
    Task<bool> UpdateCommandStatusAsync(int commandId, string status, int? processId = null, string? errorMessage = null);

    /// <summary>
    /// Планирует повторную попытку выполнения команды.
    /// Устанавливает Status='pending', инкрементирует RetryCount,
    /// устанавливает NextRetryAt (экспоненциальная задержка),
    /// сохраняет ErrorMessage последней ошибки.
    /// Возвращает новый RetryCount.
    /// </summary>
    Task<int> ScheduleRetryAsync(int commandId, DateTime nextRetryAt, string errorMessage);

    /// <summary>Освобождает команды с истёкшим таймаутом выполнения.</summary>
    Task ReleaseTimeoutCommandsAsync(int timeoutSeconds);

    /// <summary>Мягко удаляет старые сессии без pending/processing команд и возвращает их количество.</summary>
    Task<int> SoftDeleteInactiveSessionsOlderThanAsync(DateTime cutoffUtc);

    /// <summary>Возвращает команду по ID. Любой одобренный пользователь может запрашивать любую команду.</summary>
    Task<PendingCommand?> GetCommandByIdAsync(int commandId, long userId, bool isAdmin = false);

    /// <summary>
    /// Уведомляет Server о завершении сессии через Postgres LISTEN/NOTIFY.
    /// Payload: UserId|SessionId|Done|Total|ProjectName
    /// </summary>
    Task NotifyCommandCompletedAsync(long userId, int sessionId, int doneCount, int totalCount, string? projectName = null);

    /// <summary>Массовый upsert пользователей (batch через UNNEST).</summary>
    Task UpsertUsersBatchAsync(long[] userIds, int role, int status);

    /// <summary>Проверяет, есть ли среди переданных пар (команда + файл) уже существующие в очереди.</summary>
    Task<bool> HasDuplicateCommandsAsync(IEnumerable<string> commandTexts, IEnumerable<string> filePaths);

    /// <summary>Записывает отслеживаемое сообщение в БД.</summary>
    Task TrackMessageAsync(int sessionId, long chatId, int messageId);

    /// <summary>Удаляет сообщения по списку ID для сессии.</summary>
    Task DeleteTrackedMessagesAsync(int sessionId, IEnumerable<int> messageIds);

    /// <summary>Удаляет все сообщения для сессии.</summary>
    Task DeleteTrackedMessagesBySessionAsync(int sessionId);

    /// <summary>Получает все ID сообщений для сессии.</summary>
    Task<IReadOnlyList<int>> GetTrackedMessagesBySessionAsync(int sessionId);
}
