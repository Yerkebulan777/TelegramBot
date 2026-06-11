using Dapper;
using System.Text;
using System.Threading.Channels;
using TelegramBot.Data;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Infrastructure.Telegram;

/// <summary>
/// Background service: sequentially sends queued command completion notifications to Telegram.
/// </summary>
public sealed class NotificationSenderService(
    IConfiguration configuration,
    Channel<NotificationItem> notificationChannel,
    ITelegramOutputService telegramOutput,
    ILogger<NotificationSenderService> logger) : BackgroundService
{
    private readonly string _connectionString = configuration.GetConnectionString("Postgres")
        ?? "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Notification sender starting");

        try
        {
            await foreach (var item in notificationChannel.Reader.ReadAllAsync(stoppingToken))
            {
                await SendNotificationAsync(item);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Сигнализируем писателям, что читатель ушёл: blocked writers получат ChannelClosedException
            notificationChannel.Writer.TryComplete();
        }

        logger.LogInformation("Notification sender stopped");
    }

    private async Task SendNotificationAsync(NotificationItem item)
    {
        try
        {
            var prefix = string.IsNullOrEmpty(item.ProjectName) ? "" : $"{item.ProjectName} — ";
            var failed = item.Total - item.Done;
            var durationPrefix = await GetDurationPrefixAsync(item.SessionId);

            var summary = new StringBuilder();

            _=failed == 0
                ? summary.Append($"✅ {prefix}{durationPrefix}сессия завершена — все {item.Done} файлов обработано")
                : item.Done == 0
                    ? summary.Append($"❌ {prefix}{durationPrefix}сессия завершена — все {failed} файлов с ошибками")
                    : summary.Append($"⚠️ {prefix}{durationPrefix}сессия завершена: {item.Done} ✅, {failed} ❌ из {item.Total}");

            if (failed > 0)
            {
                await AppendFailedFilesAsync(summary, item.SessionId);
            }

            _=await telegramOutput.SendMessageAsync(item.UserId, summary.ToString());
            logger.LogInformation("Session completed: user={UserId}, session={SessionId}, correlationId={CorrelationId}, project={Project}, done={Done}, failed={Failed}, total={Total}",
                item.UserId, item.SessionId, item.CorrelationId, item.ProjectName, item.Done, failed, item.Total);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send queued notification for session {SessionId}, correlationId={CorrelationId}",
                item.SessionId, item.CorrelationId);
        }
    }

    private async Task AppendFailedFilesAsync(StringBuilder summary, int sessionId)
    {
        try
        {
            await using var queryConn = await NpgsqlHelper.CreateOpenConnectionAsync(_connectionString);
            var failedFiles = await queryConn.QueryAsync<string>(
                "SELECT FilePath FROM Commands WHERE SessionId = @SessionId AND Status = 'Failed'",
                new { SessionId = sessionId });

            var failedList = failedFiles.Select(f => $"- {Path.GetFileName(f)}").ToList();
            if (failedList.Count > 0)
            {
                _=summary.Append("\n\nОшибки:\n");
                _=summary.AppendJoin('\n', failedList);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to query failed files for session {SessionId}", sessionId);
        }
    }

    private async Task<string> GetDurationPrefixAsync(int sessionId)
    {
        try
        {
            await using var queryConn = await NpgsqlHelper.CreateOpenConnectionAsync(_connectionString);
            var durationSeconds = await queryConn.QuerySingleOrDefaultAsync<int?>(@"
                SELECT EXTRACT(EPOCH FROM (MAX(CompletedAt) - MIN(StartedAt)))::int
                FROM Commands
                WHERE SessionId = @SessionId
                  AND Status != 'Deleted'
                  AND StartedAt IS NOT NULL
                  AND CompletedAt IS NOT NULL;",
                new { SessionId = sessionId });

            return durationSeconds is > 0
                ? $"{FormatDuration(durationSeconds.Value)} — "
                : "";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to query duration for session {SessionId}", sessionId);
            return "";
        }
    }

    private static string FormatDuration(int totalSeconds)
    {
        var duration = TimeSpan.FromSeconds(totalSeconds);

        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours} ч {duration.Minutes:D2} мин"
            : duration.TotalMinutes >= 1 ? $"{duration.Minutes} мин {duration.Seconds:D2} с" : $"{duration.Seconds} с";
    }
}
