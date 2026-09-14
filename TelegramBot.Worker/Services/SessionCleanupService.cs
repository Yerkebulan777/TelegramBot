using Microsoft.Extensions.Options;
using TelegramBot.Core.Config;
using TelegramBot.Data;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Фоновый сервис автоматической очистки завершённых сессий.
/// Периодически помечает как Deleted сессии старше CompletedSessionRetentionDays дней,
/// у которых нет pending/processing команд.
/// Если CompletedSessionRetentionDays &lt;= 0 — очистка отключена.
/// </summary>
public sealed class SessionCleanupService(
    SessionDataService sessionDataService,
    SchemaReadyGate schemaReadyGate,
    IOptions<WorkerOptions> workerOptions,
    ILogger<SessionCleanupService> logger) : BackgroundService
{
    private readonly WorkerOptions _options = workerOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = _options.CleanupIntervalSeconds;
        if (intervalSeconds <= 0)
        {
            logger.LogWarning("Cleanup disabled: interval={Interval}s", intervalSeconds);
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        logger.LogInformation(
            "Session cleanup: retention={RetentionDays}d, interval={Interval}s",
            _options.CompletedSessionRetentionDays, intervalSeconds);
        await schemaReadyGate.WaitAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _=await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await RunCleanupCycleAsync(stoppingToken);
        }
    }

    private async Task RunCleanupCycleAsync(CancellationToken ct)
    {
        try
        {
            var retentionDays = _options.CompletedSessionRetentionDays;
            if (retentionDays <= 0)
            {
                return;
            }

            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            var deleted = await sessionDataService.SoftDeleteInactiveSessionsOlderThanAsync(cutoff);

            if (deleted > 0)
            {
                logger.LogInformation(
                    "Cleaned {Count} inactive sessions <{Cutoff:yyyy-MM-dd} ({RetentionDays}d)",
                    deleted, cutoff, retentionDays);
            }
        }
        catch (OperationCanceledException)
        {
            // штатное завершение
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Session cleanup cycle failed");
        }
    }
}
