using TelegramBot.Server.Helpers;
using Microsoft.Extensions.Options;
using Npgsql;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using TelegramBot.Core.Config;
using TelegramBot.Core.Models;
using TelegramBot.Data;
using TelegramBot.Data.Models;

namespace TelegramBot.Server.Services.Infrastructure.Telegram;

/// <summary>One supervised polling loop owns durable notification delivery.</summary>
public sealed class NotificationSenderService(
    SessionDataService sessionDataService,
    NotificationOutboxDataService notificationOutboxDataService,
    TelegramOutputService telegramOutput,
    SchemaReadyGate schemaReadyGate,
    IOptions<MessageCleanupOptions> cleanupOptions,
    ILogger<NotificationSenderService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    private DateTime _nextReconciliationAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Notification sender start");
        await schemaReadyGate.WaitAsync(stoppingToken);
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            do
            {
                try
                {
                    await DrainAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Notification cycle failed; retrying on next poll");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        logger.LogInformation("Notification sender stop");
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        await using var lockHolder = await notificationOutboxDataService.TryAcquireSenderLockAsync(cancellationToken);
        if (lockHolder == null)
        {
            return;
        }
        if (DateTime.UtcNow >= _nextReconciliationAt)
        {
            await notificationOutboxDataService.ReconcileAsync(lockHolder, cancellationToken);
            _nextReconciliationAt = DateTime.UtcNow.AddMinutes(1);
        }
        // Bound each drain so one busy replica does not hold the sender lock indefinitely.
        for (var count = 0; count < 20; count++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await notificationOutboxDataService.SuppressObsoleteAsync(lockHolder, cancellationToken);
            var item = await notificationOutboxDataService.ClaimPendingAsync(lockHolder, LeaseDuration, cancellationToken);
            if (item == null)
            {
                return;
            }
            await SendAsync(item, cancellationToken);
        }
    }

    private async Task SendAsync(NotificationOutboxItem item, CancellationToken cancellationToken)
    {
        Message sent;
        try
        {
            using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sendTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            sent = item.EventType == NotificationOutboxDataService.SessionCompletedEvent
                ? await SendCompletionNotificationAsync(item.SessionId, item.CorrelationId, sendTimeout.Token)
                : await telegramOutput.SendNotificationAsync(item.UserId, "⚙️ Задание запущено", sendTimeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var permanent = ex is ApiRequestException { ErrorCode: 400 or 403 };
            var retryAfter = ex is ApiRequestException { ErrorCode: 429 } rateLimit
                ? Math.Max(1, rateLimit.Parameters?.RetryAfter ?? 5)
                : Math.Min(300, Math.Max(5, item.Attempts * 10));
            await notificationOutboxDataService.MarkFailedAsync(item.OutboxId, retryAfter, permanent, ex, cancellationToken);
            logger.LogWarning(ex, "Notification delivery failed: outboxId={OutboxId}, session={SessionId}, permanent={Permanent}, retrySeconds={RetrySeconds}",
                item.OutboxId, item.SessionId, permanent, retryAfter);
            if (ex is ApiRequestException { ErrorCode: 429 })
            {
                // Keep the shared sender lock during Telegram's cooldown so another replica
                // cannot immediately resume this bot's notification traffic.
                await Task.Delay(TimeSpan.FromSeconds(retryAfter), cancellationToken);
            }
            return;
        }

        // Telegram acknowledged this message: retry only the DB acknowledgement, never send again here.
        // The unavoidable crash window between the two systems still permits duplicates after restart.
        var now = DateTime.UtcNow;
        var deleteAfter = item.EventType == NotificationOutboxDataService.SessionCompletedEvent
            ? now.AddHours(cleanupOptions.Value.RetentionHours)
            : now.AddMinutes(cleanupOptions.Value.TemporaryRetentionMinutes);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await notificationOutboxDataService.MarkSentAsync(item, sent.Chat.Id, sent.MessageId, sent.Date, deleteAfter, cancellationToken);
                logger.LogInformation("Notification delivered: outboxId={OutboxId}, session={SessionId}, event={EventType}, message={MessageId}",
                    item.OutboxId, item.SessionId, item.EventType, sent.MessageId);
                return;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                ex is NpgsqlException { IsTransient: true } or TimeoutException or OperationCanceledException)
            {
                logger.LogWarning(ex, "Notification acknowledgement deferred: outboxId={OutboxId}, message={MessageId}",
                    item.OutboxId, sent.MessageId);
                await Task.Delay(PollInterval, cancellationToken);
            }
        }
    }

    private async Task<Message> SendCompletionNotificationAsync(int sessionId, string correlationId, CancellationToken cancellationToken)
    {
        var session = await sessionDataService.GetSessionCompletionSummaryAsync(sessionId);
        // Сводка failed-команд для трассировки: дошли ли причины из БД до уведомления.
        var failedWithMsg = session.Failed.Count(command => !string.IsNullOrWhiteSpace(command.ErrorMessage));
        logger.LogInformation(
            "Notify summary: session={SessionId}, corr={CorrelationId}, done={Done}, failed={Failed}, failedWithMsg={FailedWithMsg}, warned={Warned}, total={Total}",
            sessionId, correlationId, session.DoneFiles, session.FailedFiles, failedWithMsg, session.Warned.Count(), session.TotalFiles);

        if (logger.IsEnabled(LogLevel.Debug))
        {
            foreach (var failed in session.Failed)
            {
                logger.LogDebug("Notify failed detail: session={SessionId}, file={File}, err={Msg}",
                    sessionId, Path.GetFileName(failed.FilePath), failed.ErrorMessage ?? "<null>");
            }
        }

        var completionMessage = await telegramOutput.SendNotificationAsync(session.UserId, CompletionMessageFormatter.Format(session), cancellationToken);

        logger.LogInformation("Completion accepted by Telegram: user={Username} ({UserId}), session={SessionId}, corr={CorrelationId}, project={Project}, done={Done}, failed={Failed}, total={Total}",
            session.Username ?? "(unnamed)", session.UserId, sessionId, correlationId, session.ProjectName, session.DoneFiles, session.FailedFiles, session.TotalFiles);
        return completionMessage;
    }
}
