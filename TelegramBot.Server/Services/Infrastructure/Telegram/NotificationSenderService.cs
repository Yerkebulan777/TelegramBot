using System.Text;
using System.Threading.Channels;
using TelegramBot.Data;
using TelegramBot.Data.Models;
using TelegramBot.Server.Models;

namespace TelegramBot.Server.Services.Infrastructure.Telegram;

/// <summary>
/// Background service: sequentially sends queued command completion notifications to Telegram.
/// </summary>
public sealed class NotificationSenderService(
    Channel<NotificationItem> notificationChannel,
    SessionDataService sessionDataService,
    NotificationOutboxDataService notificationOutboxDataService,
    TelegramOutputService telegramOutput,
    ILogger<NotificationSenderService> logger) : BackgroundService
{
    private static readonly TimeSpan OutboxLeaseDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OutboxPollInterval = TimeSpan.FromSeconds(30);
    private const int OutboxBatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Notification sender start");

        try
        {
            await DrainCompletionOutboxAsync(stoppingToken);
            _ = RunOutboxPollingAsync(stoppingToken);

            await foreach (var item in notificationChannel.Reader.ReadAllAsync(stoppingToken))
            {
                await SendNotificationItemAsync(item, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Сигнализируем писателям, что читатель ушёл: blocked writers получат ChannelClosedException
            _=notificationChannel.Writer.TryComplete();
        }

        logger.LogInformation("Notification sender stop");
    }

    private async Task RunOutboxPollingAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(OutboxPollInterval);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await DrainCompletionOutboxAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Outbox polling stopped");
        }
    }

    private async Task SendNotificationItemAsync(NotificationItem item, CancellationToken stoppingToken)
    {
        try
        {
            if (item.DrainCompletionOutbox)
            {
                await DrainCompletionOutboxAsync(stoppingToken);
                return;
            }

            if (item.UserId.HasValue)
            {
                _=await telegramOutput.SendMessageAsync(item.UserId.Value, "⚙️ Задание запущено");
                var startedUsername = await sessionDataService.GetSessionUsernameAsync(item.SessionId!.Value) ?? "(unnamed)";
                logger.LogInformation("Session started notify: user={Username} ({UserId}), session={SessionId}, corr={CorrelationId}",
                    startedUsername, item.UserId.Value, item.SessionId, item.CorrelationId);
                return;
            }

            logger.LogWarning("Notification ignored: unknown_shape");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Send queued notify fail: session={SessionId}, corr={CorrelationId}",
            item.SessionId, item.CorrelationId);
        }
    }

    private async Task DrainCompletionOutboxAsync(CancellationToken stoppingToken)
    {
        // Single-writer mutual exclusion: только одна реплика Server одновременно drain'ит outbox.
        // Session-level advisory lock удерживается на весь drain-цикл; отпускается через await using.
        // При multi-instance вторая реплика получает null и пропускает цикл — её polling tick
        // (30 сек) повторит попытку. Это устраняет гонку между репликами при перекрывающихся окнах LockedUntil.
        await using var lockHolder = await notificationOutboxDataService.TryAcquireSenderLockAsync();
        if (lockHolder == null)
        {
            logger.LogDebug("Outbox drain skipped: lock held by another replica");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var items = await notificationOutboxDataService.ClaimPendingAsync(
                NotificationOutboxDataService.SessionCompletedEvent,
                OutboxBatchSize,
                OutboxLeaseDuration);

            if (items.Count == 0)
            {
                return;
            }

            foreach (var item in items)
            {
                await SendCompletionOutboxItemAsync(item);
            }
        }
    }

    private async Task SendCompletionOutboxItemAsync(NotificationOutboxItem item)
    {
        try
        {
            await SendCompletionNotificationAsync(item.SessionId, item.CorrelationId);
            await notificationOutboxDataService.MarkSentAsync(item.OutboxId);
        }
        catch (Exception ex)
        {
            await notificationOutboxDataService.MarkFailedAsync(item.OutboxId, item.Attempts, ex);
            logger.LogError(ex, "Outbox send fail: outboxId={OutboxId}, session={SessionId}, corr={CorrelationId}, attempts={Attempts}",
                item.OutboxId, item.SessionId, item.CorrelationId, item.Attempts);
        }
    }

    private async Task SendCompletionNotificationAsync(int sessionId, string correlationId)
    {
        var session = await sessionDataService.GetSessionCompletionSummaryAsync(sessionId);
        var prefix = string.IsNullOrEmpty(session.ProjectName) ? "" : $"{session.ProjectName} — ";
        var durationPrefix = FormatDurationPrefix(session.DurationSeconds);

        var summary = new StringBuilder();

        _=session.FailedFiles == 0
            ? summary.Append($"✅ {prefix}{durationPrefix}сессия завершена — все {session.DoneFiles} файлов обработано")
            : session.DoneFiles == 0
                ? summary.Append($"❌ {prefix}{durationPrefix}сессия завершена — все {session.FailedFiles} файлов с ошибками")
                : summary.Append($"⚠️ {prefix}{durationPrefix}сессия завершена: {session.DoneFiles} ✅, {session.FailedFiles} ❌ из {session.TotalFiles}");

        if (session.FailedFiles > 0 && session.FailedFilePaths.Count > 0)
        {
            _=summary.Append("\n\nОшибки:\n");
            for (var i = 0; i < session.FailedFilePaths.Count; i++)
            {
                if (i > 0)
                {
                    _ = summary.AppendLine();
                }

                _ = summary.Append("- ").Append(Path.GetFileName(session.FailedFilePaths[i]));
            }
        }

        _=await telegramOutput.SendMessageAsync(session.UserId, summary.ToString());
        logger.LogInformation("Completion sent: user={Username} ({UserId}), session={SessionId}, corr={CorrelationId}, project={Project}, done={Done}, failed={Failed}, total={Total}",
            session.Username ?? "(unnamed)", session.UserId, sessionId, correlationId, session.ProjectName, session.DoneFiles, session.FailedFiles, session.TotalFiles);
    }

    private static string FormatDurationPrefix(int? durationSeconds)
    {
        return durationSeconds is > 0 ? $"{FormatDuration(durationSeconds.Value)} — " : "";
    }

    private static string FormatDuration(int totalSeconds)
    {
        var duration = TimeSpan.FromSeconds(totalSeconds);

        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours} ч {duration.Minutes:D2} мин"
            : duration.TotalMinutes >= 1 ? $"{duration.Minutes} мин {duration.Seconds:D2} с" : $"{duration.Seconds} с";
    }
}
