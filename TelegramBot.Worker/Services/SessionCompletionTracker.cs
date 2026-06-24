using TelegramBot.Core.Models;
using TelegramBot.Data;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Отслеживает завершение сессий и отправляет уведомление пользователю,
/// когда все команды сессии выполнены.
/// Проверяет БД напрямую, без in-memory счётчика — корректно работает
/// при любом batch size и нескольких воркерах.
/// </summary>
public sealed class SessionCompletionTracker(
    SessionDataService sessionDataService,
    ILogger<SessionCompletionTracker> logger)
{
    /// <summary>
    /// Вызывается при завершении команды (Done/Failed/retry).
    /// Проверяет БД: если не осталось pending/processing команд — отправляет уведомление.
    /// </summary>
    public async Task OnCommandCompletedAsync(PendingCommand cmd)
    {
        try
        {
            var remainingInDb = await sessionDataService.CountPendingProcessingBySessionAsync(cmd.SessionId);
            if (remainingInDb > 0)
            {
                logger.LogDebug(
                    "Session {SessionId}: {Remaining} commands still pending/processing, skipping notification, correlationId={CorrelationId}",
                    cmd.SessionId, remainingInDb, cmd.CorrelationId);
                return;
            }

            var notified = await sessionDataService.NotifySessionCompletedOnceAsync(cmd.SessionId, cmd.CorrelationId);
            if (!notified)
            {
                logger.LogDebug(
                    "Session {SessionId}: completion notification already sent or session deleted, correlationId={CorrelationId}",
                    cmd.SessionId, cmd.CorrelationId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to notify session completion: session={SessionId}, correlationId={CorrelationId}",
                cmd.SessionId, cmd.CorrelationId);
        }
    }
}
