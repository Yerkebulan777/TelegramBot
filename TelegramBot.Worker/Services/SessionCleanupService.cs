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
    IOptions<WorkerOptions> workerOptions,
    ILogger<SessionCleanupService> logger) : BackgroundService
{
    private readonly WorkerOptions _options = workerOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = _options.CleanupIntervalSeconds;
        if (intervalSeconds <= 0)
        {
            logger.LogWarning("CleanupIntervalSeconds = {Interval}, auto-cleanup disabled", intervalSeconds);
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        logger.LogInformation(
            "Session cleanup started: retentionDays={RetentionDays}, interval={Interval}s",
            _options.CompletedSessionRetentionDays, intervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
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
                return;

            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            var deleted = await sessionDataService.SoftDeleteInactiveSessionsOlderThanAsync(cutoff);

            if (deleted > 0)
            {
                logger.LogInformation(
                    "Auto-cleaned {Count} inactive session(s) older than {Cutoff:yyyy-MM-dd} ({RetentionDays}d retention)",
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
