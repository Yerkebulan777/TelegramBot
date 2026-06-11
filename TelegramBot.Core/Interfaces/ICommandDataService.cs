using TelegramBot.Core.Models;

namespace TelegramBot.Core.Interfaces;

/// <summary>
/// Service for command data persistence.
/// </summary>
public interface ICommandDataService
{
    /// <summary>Забирает pending команды для выполнения.</summary>
    Task<IReadOnlyList<PendingCommand>> ClaimPendingCommandsAsync(int limit = 50, int leaseTimeoutMinutes = 5);

    /// <summary>Освобождает истёкшие leases.</summary>
    Task ReleaseExpiredLeasesAsync();

    /// <summary>Обновляет статус команды.</summary>
    Task<bool> UpdateCommandStatusAsync(int commandId, string status, int? processId = null, string? errorMessage = null, int? progress = null, string? result = null);

    /// <summary>Обновляет прогресс команды.</summary>
    Task<bool> UpdateCommandProgressAsync(int commandId, int progress);

    /// <summary>Обновляет результат команды.</summary>
    Task<bool> UpdateCommandResultAsync(int commandId, string result);

    /// <summary>Планирует повторную попытку.</summary>
    Task<int> ScheduleRetryAsync(int commandId, DateTime nextRetryAt, string errorMessage);

    /// <summary>Возвращает команду по ID.</summary>
    Task<PendingCommand?> GetCommandByIdAsync(int commandId, long userId, bool isAdmin = false);

    /// <summary>Мягкое удаление команды.</summary>
    Task<bool> DeleteCommandAsync(int commandId, long userId, bool isAdmin = false);

    /// <summary>Мягкое удаление команд по типу.</summary>
    Task<int> DeleteCommandsByTypeAsync(int sessionId, string commandType);

    /// <summary>Проверяет наличие дубликатов команд.</summary>
    Task<bool> HasDuplicateCommandsAsync(IEnumerable<string> commandTexts, IEnumerable<string> filePaths);
}
