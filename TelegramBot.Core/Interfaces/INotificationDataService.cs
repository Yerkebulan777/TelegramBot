namespace TelegramBot.Core.Interfaces;

/// <summary>
/// Service for PostgreSQL notifications.
/// </summary>
public interface INotificationDataService
{
    /// <summary>Отправляет уведомление о завершении команды.</summary>
    Task NotifyCommandCompletedAsync(
        long userId,
        int sessionId,
        string correlationId,
        int doneCount,
        int totalCount,
        string? projectName = null);
}
