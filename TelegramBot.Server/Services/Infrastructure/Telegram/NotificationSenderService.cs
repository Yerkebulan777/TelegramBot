using System.Text;
using System.Threading.Channels;
using TelegramBot.Data;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Infrastructure.Telegram;

/// <summary>
/// Background service: sequentially sends queued command completion notifications to Telegram.
/// </summary>
public sealed class NotificationSenderService(
    Channel<NotificationItem> notificationChannel,
    SessionDataService sessionDataService,
    ITelegramOutputService telegramOutput,
    ILogger<NotificationSenderService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Notification sender starting");

        try
        {
            await foreach (var item in notificationChannel.Reader.ReadAllAsync(stoppingToken))
            {
                await SendNotificationItemAsync(item);
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

        logger.LogInformation("Notification sender stopped");
    }

    private async Task SendNotificationItemAsync(NotificationItem item)
    {
        try
        {
            if (item.UserId.HasValue)
            {
                _=await telegramOutput.SendMessageAsync(item.UserId.Value, "⚙️ Задание запущено");
                logger.LogInformation("Session started notify sent: user={UserId}, session={SessionId}, correlationId={CorrelationId}",
                    item.UserId.Value, item.SessionId, item.CorrelationId);
                return;
            }

            var session = await sessionDataService.GetSessionCompletionSummaryAsync(item.SessionId);
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
                _=summary.AppendJoin('\n', session.FailedFilePaths.Select(f => $"- {Path.GetFileName(f)}"));
            }

            _=await telegramOutput.SendMessageAsync(session.UserId, summary.ToString());
            logger.LogInformation("Session completed: user={UserId}, session={SessionId}, correlationId={CorrelationId}, project={Project}, done={Done}, failed={Failed}, total={Total}",
                session.UserId, item.SessionId, item.CorrelationId, session.ProjectName, session.DoneFiles, session.FailedFiles, session.TotalFiles);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send queued notification for session {SessionId}, correlationId={CorrelationId}",
            item.SessionId, item.CorrelationId);
        }
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
