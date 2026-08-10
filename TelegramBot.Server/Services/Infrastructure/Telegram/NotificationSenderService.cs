using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using TelegramBot.Core.Models;
using TelegramBot.Data;
using TelegramBot.Data.Models;
using TelegramBot.Server.Models;
using TelegramBot.Server.Services.Application;

namespace TelegramBot.Server.Services.Infrastructure.Telegram;

/// <summary>
/// Background service: sequentially sends queued command completion notifications to Telegram.
/// </summary>
public sealed class NotificationSenderService(
    Channel<NotificationItem> notificationChannel,
    SessionDataService sessionDataService,
    NotificationOutboxDataService notificationOutboxDataService,
    TelegramOutputService telegramOutput,
    MessageTrackingService messageTrackingService,
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
            // Стартовый drain изолирован: если БД ещё не готова (Postgres в Docker
            // поднимается позже автозапуска службы), здесь бросит — но основной цикл
            // всё равно должен жить, иначе до поднятия БД сервис умрёт безвозвратно.
            // Outbox retry'ется polling'ом (RunOutboxPollingAsync) и в SendNotificationItemAsync.
            try
            {
                await DrainCompletionOutboxAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox startup drain failed; retrying via polling");
            }

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
                var startedMessage = await telegramOutput.SendMessageAsync(item.UserId.Value, "⚙️ Задание запущено");
                await messageTrackingService.TrackAsync(startedMessage, item.SessionId!.Value);
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
            // SendCompletionNotificationAsync возвращает Message?, которое ExecuteWithRetryAsync
            // отдаёт как null при исчерпании ретраев (429, сетевые) без исключения. null означает
            // НЕдоставку: трактуем как fail, чтобы outbox сохранил at-least-once (MarkSent только при
            // подтверждённой доставке).
            var sent = await SendCompletionNotificationAsync(item.SessionId, item.CorrelationId);
            if (sent == null)
            {
                throw new InvalidOperationException(
                    "Completion notification not delivered: SendMessageAsync returned null after retries");
            }

            await notificationOutboxDataService.MarkSentAsync(item.OutboxId);
        }
        catch (Exception ex)
        {
            await notificationOutboxDataService.MarkFailedAsync(item.OutboxId, item.Attempts, ex);
            logger.LogError(ex, "Outbox send fail: outboxId={OutboxId}, session={SessionId}, corr={CorrelationId}, attempts={Attempts}",
                item.OutboxId, item.SessionId, item.CorrelationId, item.Attempts);
        }
    }

    private async Task<Message?> SendCompletionNotificationAsync(int sessionId, string correlationId)
    {
        var session = await sessionDataService.GetSessionCompletionSummaryAsync(sessionId);
        var prefix = string.IsNullOrEmpty(session.ProjectName) ? "" : $"{session.ProjectName} — ";
        var durationPrefix = FormatDurationPrefix(session.DurationSeconds);

        var summary = new StringBuilder();

        _=session.FailedFiles == 0
            ? session.WarnedCommands.Count == 0
                ? summary.Append($"✅ {prefix}{durationPrefix}сессия завершена — все {session.DoneFiles} файлов обработано")
                : summary.Append($"⚠️ {prefix}{durationPrefix}сессия завершена — все {session.DoneFiles} файлов обработано (есть предупреждения)")
            : session.DoneFiles == 0
                ? summary.Append($"❌ {prefix}{durationPrefix}сессия завершена — все {session.FailedFiles} файлов с ошибками")
                : summary.Append($"⚠️ {prefix}{durationPrefix}сессия завершена: {session.DoneFiles} ✅, {session.FailedFiles} ❌ из {session.TotalFiles}");

        if (session.FailedFiles > 0 && session.FailedCommands.Count > 0)
        {
            AppendCommandNotes(summary, "Ошибки:", session.FailedCommands);
        }

        if (session.WarnedCommands.Count > 0)
        {
            AppendCommandNotes(summary, "Предупреждения:", session.WarnedCommands);
        }

        // Сводка failed-команд для трассировки: дошли ли причины из БД до уведомления.
        var failedWithMsg = session.FailedCommands.Count(c => !string.IsNullOrWhiteSpace(c.ErrorMessage));
        logger.LogInformation(
            "Notify summary: session={SessionId}, corr={CorrelationId}, done={Done}, failed={Failed}, failedWithMsg={FailedWithMsg}, warned={Warned}, total={Total}",
            sessionId, correlationId, session.DoneFiles, session.FailedFiles, failedWithMsg, session.WarnedCommands.Count, session.TotalFiles);

        if (logger.IsEnabled(LogLevel.Debug))
        {
            foreach (var failed in session.FailedCommands)
            {
                logger.LogDebug("Notify failed detail: session={SessionId}, file={File}, err={Msg}",
                    sessionId, Path.GetFileName(failed.FilePath), failed.ErrorMessage ?? "<null>");
            }
        }

        var completionMessage = await telegramOutput.SendMessageAsync(session.UserId, ClampToTelegramLimit(summary));
        await messageTrackingService.TrackAsync(completionMessage, sessionId);
        logger.LogInformation("Completion sent: user={Username} ({UserId}), session={SessionId}, corr={CorrelationId}, project={Project}, done={Done}, failed={Failed}, total={Total}",
            session.Username ?? "(unnamed)", session.UserId, sessionId, correlationId, session.ProjectName, session.DoneFiles, session.FailedFiles, session.TotalFiles);
        return completionMessage;
    }

    /// <summary>Максимум пунктов с ошибками в одном сообщении; остальные схлопываются в «и ещё N».</summary>
    private const int MaxFailedFilesInMessage = 15;

    /// <summary>Лимит длины причины сбоя на один файл — чтобы стек/портянка не раздула сообщение.</summary>
    private const int MaxReasonLength = 200;

    /// <summary>Жёсткий лимит текста под потолок Telegram (4096) с запасом на маркер обрыва.</summary>
    private const int MaxMessageLength = 4000;

    private static void AppendCommandNotes(StringBuilder summary, string title, List<FailedCommandInfo> commands)
    {
        _=summary.Append("\n\n").Append(title).Append('\n');

        var shown = Math.Min(commands.Count, MaxFailedFilesInMessage);
        for (var i = 0; i < shown; i++)
        {
            if (i > 0)
            {
                _ = summary.AppendLine();
            }

            _ = summary.Append("- ").Append(Path.GetFileName(commands[i].FilePath));

            var reason = FormatReason(commands[i].ErrorMessage);
            if (reason.Length > 0)
            {
                _ = summary.Append(" — ").Append(reason);
            }
        }

        if (commands.Count > MaxFailedFilesInMessage)
        {
            _ = summary.Append("\n…и ещё ").Append(commands.Count - MaxFailedFilesInMessage);
        }
    }

    /// <summary>Оставляет первую строку причины (без стека) и обрезает до <see cref="MaxReasonLength"/>.</summary>
    private static string FormatReason(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return string.Empty;
        }

        // Стек/детали обычно идут с переноса — пользователю нужна только первая строка (суть ошибки).
        var firstLine = errorMessage.AsSpan();
        var newline = firstLine.IndexOfAny('\r', '\n');
        if (newline >= 0)
        {
            firstLine = firstLine[..newline];
        }

        return firstLine.Length > MaxReasonLength
            ? $"{firstLine[..MaxReasonLength]}…"
            : firstLine.ToString();
    }

    /// <summary>Гарантирует, что сообщение не превысит лимит Telegram — иначе SendAsync выбросит исключение.</summary>
    private static string ClampToTelegramLimit(StringBuilder summary)
    {
        if (summary.Length <= MaxMessageLength)
        {
            return summary.ToString();
        }

        return summary.ToString(0, MaxMessageLength - 1) + "…";
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
