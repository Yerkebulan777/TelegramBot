using Microsoft.Extensions.Options;
using TelegramBot.Core.Config;
using TelegramBot.Data;

namespace TelegramBot.Server.Services.Infrastructure.Telegram;

/// <summary>
/// Удаляет сообщения, которые пользователь не успел очистить следующим взаимодействием с ботом.
/// </summary>
public sealed class TrackedMessageCleanupService(
    IOptions<MessageCleanupOptions> cleanupOptions,
    MessageTrackingDataService messageTrackingDataService,
    TelegramOutputService telegramOutputService,
    ILogger<TrackedMessageCleanupService> logger) : BackgroundService
{
    private readonly MessageCleanupOptions _options = cleanupOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Tracked message cleanup disabled");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.IntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunCleanupCycleAsync(stoppingToken);

            try
            {
                _ = await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunCleanupCycleAsync(CancellationToken stoppingToken)
    {
        try
        {
            var now = DateTime.UtcNow;
            var expiredCount = await messageTrackingDataService.DeleteTrackedMessagesOlderThanAsync(
                now.AddHours(-_options.MaximumDeletionAgeHours));
            if (expiredCount > 0)
            {
                logger.LogWarning(
                    "Tracked messages expired before cleanup: count={Count}, maximumAgeHours={MaximumAgeHours}",
                    expiredCount,
                    _options.MaximumDeletionAgeHours);
            }

            var trackedMessages = await messageTrackingDataService.GetTrackedMessagesForCleanupAsync(
                now.AddHours(-_options.RetentionHours),
                now.AddHours(-_options.MaximumDeletionAgeHours),
                _options.BatchSize);
            if (trackedMessages.Count == 0)
            {
                return;
            }

            var deletedCount = await telegramOutputService.CleanupTrackedMessagesAsync(trackedMessages, stoppingToken);
            logger.LogDebug("Tracked message cleanup cycle finished: selectedCount={SelectedCount}, deletedCount={DeletedCount}",
                trackedMessages.Count, deletedCount);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Tracked message cleanup cycle failed");
        }
    }
}
