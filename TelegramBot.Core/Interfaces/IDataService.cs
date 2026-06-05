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

    /// <summary>Soft-delete отменённых команд, завершённых более указанного количества дней назад.</summary>
    Task CleanupOldCancelledCommandsAsync(int olderThanDays);

    /// <summary>Отменяет команду: обновляет статус на 'Cancelled'.</summary>
    Task<bool> CancelCommandAsync(int commandId, long userId);

    /// <summary>Уведомляет Worker о необходимости отменить команду через NOTIFY command_cancel.</summary>
    Task NotifyCommandCancelAsync(int commandId);

    /// <summary>Возвращает команду по ID (для проверки принадлежности пользователю).</summary>
    Task<PendingCommand?> GetCommandByIdAsync(int commandId, long userId);

    /// <summary>Уведомляет Worker-ов о новых командах через Postgres LISTEN/NOTIFY.</summary>
    Task NotifyNewCommandsAsync(int sessionId);

    /// <summary>
    /// Уведомляет Server о завершении/ошибке команды через Postgres LISTEN/NOTIFY.
    /// Payload: UserId|CommandId|CommandText|Status|ErrorMessage
    /// </summary>
    Task NotifyCommandCompletedAsync(long userId, int commandId, string commandText, string status, string? errorMessage);

    /// <summary>Сохраняет ID сообщений для отложенной очистки (прерванный диалог).</summary>
    Task SaveTrackedMessagesAsync(long userId, IEnumerable<int> messageIds);
    Task SaveTrackedMessageAsync(long userId, int messageId);

    /// <summary>Возвращает все сохранённые ID сообщений, сгруппированные по userId.</summary>
    Task<ILookup<long, int>> GetAllTrackedMessagesAsync();

    /// <summary>Возвращает сохранённые ID сообщений пользователя для очистки.</summary>
    Task<IReadOnlyList<int>> GetTrackedMessagesAsync(long userId);

    /// <summary>Удаляет конкретное сохранённое сообщение.</summary>
    Task DeleteTrackedMessageAsync(long userId, int messageId);

    /// <summary>Удаляет несколько сообщений за один batch-запрос.</summary>
    Task DeleteTrackedMessagesBatchAsync(long userId, int[] messageIds);

    /// <summary>Удаляет все сохранённые ID сообщений для пользователя.</summary>
    Task DeleteTrackedMessagesAsync(long userId);
}
