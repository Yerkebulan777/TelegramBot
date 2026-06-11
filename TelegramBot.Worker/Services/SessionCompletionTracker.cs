using System.Collections.Concurrent;
using TelegramBot.Core.Models;
using TelegramBot.Data;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Отслеживает завершение сессий через in-memory счётчик оставшихся команд.
/// При обнулении batch-счётчика для сессии проверяет БД на предмет
/// оставшихся pending/processing команд и отправляет уведомление пользователю
/// только когда вся сессия действительно завершена.
/// </summary>
public sealed class SessionCompletionTracker(
    SessionDataService sessionDataService,
    ILogger<SessionCompletionTracker> logger)
{
    // Счётчик оставшихся команд по сессиям — избегает лишних SQL запросов
    private readonly ConcurrentDictionary<int, int> _sessionRemaining = new();

    /// <summary>
    /// Регистрирует захваченные команды в счётчике сессий.
    /// Вызывается после ClaimPendingCommandsAsync.
    /// </summary>
    public void TrackClaimedCommands(IReadOnlyList<PendingCommand> claimed)
    {
        foreach (var group in claimed.GroupBy(c => c.SessionId))
        {
            _ = _sessionRemaining.AddOrUpdate(group.Key, group.Count(), (_, existing) => existing + group.Count());
        }
    }

    /// <summary>
    /// Декрементирует счётчик сессии. Если текущий batch по сессии закончился (счётчик == 0),
    /// проверяет БД и отправляет уведомление при полном завершении сессии.
    /// </summary>
    public async Task OnCommandCompletedAsync(PendingCommand cmd)
    {
        // AddOrUpdate атомарен: только поток, получивший 0, проверяет БД
        var newRemaining = _sessionRemaining.AddOrUpdate(
            cmd.SessionId,
            _ => 0,
            (_, current) => current - 1);

        if (newRemaining != 0)
        {
            return; // batch по сессии ещё не закончился
        }

        _ = _sessionRemaining.TryRemove(cmd.SessionId, out _);

        try
        {
            // Проверяем БД: если ещё есть pending/processing — сессия не завершена.
            // Это корректно обрабатывает случай, когда команд > DefaultBatchSize или несколько воркеров.
            var remainingInDb = await sessionDataService.CountPendingProcessingBySessionAsync(cmd.SessionId);
            if (remainingInDb > 0)
            {
                logger.LogDebug(
                    "Session {SessionId}: counter zero but {Remaining} commands still pending/processing in DB, skipping notification, correlationId={CorrelationId}",
                    cmd.SessionId, remainingInDb, cmd.CorrelationId);
                return;
            }

            await sessionDataService.NotifySessionCompletedAsync(cmd.SessionId, cmd.CorrelationId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to notify session completion: session={SessionId}, correlationId={CorrelationId}",
                cmd.SessionId, cmd.CorrelationId);
        }
    }
}
