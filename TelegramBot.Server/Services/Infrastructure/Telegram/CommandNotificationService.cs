using Dapper;
using Npgsql;
using System.Text;
using TelegramBot.Data;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Infrastructure.Telegram;

/// <summary>
/// Background service: слушает PostgreSQL LISTEN/NOTIFY на канале 'command_completed'.
/// При получении уведомления отправляет пользователю сводку по завершённой сессии.
/// </summary>
public sealed class CommandNotificationService(
    IConfiguration configuration,
    ITelegramOutputService telegramOutput,
    ILogger<CommandNotificationService> logger) : BackgroundService
{
    private const int ReconnectDelayMs = 5_000;

    private readonly string _connectionString = configuration.GetConnectionString("Postgres")
        ?? "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Command notifications starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunListenerLoopAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Command notifications lost: retryMs={Delay}", ReconnectDelayMs);
                await Task.Delay(ReconnectDelayMs, stoppingToken);
            }
        }

        logger.LogInformation("Command notifications stopped");
    }

    private async Task RunListenerLoopAsync(CancellationToken stoppingToken)
    {
        await using var conn = await NpgsqlHelper.CreateOpenConnectionAsync(_connectionString, stoppingToken);

        _=await conn.ExecuteAsync("LISTEN command_completed;");
        conn.Notification += OnNotificationReceived;

        logger.LogInformation("Command notifications listening: channel=command_completed");

        // Держим соединение открытым — WaitAsync блокируется до получения NOTIFY
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await conn.WaitAsync(stoppingToken);
            }
            catch (NpgsqlException ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Command notifications error: source=postgres");
                break;
            }
        }
    }

    private void OnNotificationReceived(object sender, NpgsqlNotificationEventArgs e)
    {
        // Fire-and-forget: событие Npgsql требует void, асинхронная работа — в HandleNotificationAsync
        _ = HandleNotificationAsync(e);
    }

    private async Task HandleNotificationAsync(NpgsqlNotificationEventArgs e)
    {
        try
        {
            if (e.Payload == null)
            {
                return;
            }

            // Payload: UserId|SessionId|Done|Total|ProjectName
            var parts = e.Payload.Split('|', 5);
            if (parts.Length < 4)
            {
                logger.LogWarning("Completion notify ignored: reason=invalid_payload");
                return;
            }

            if (!long.TryParse(parts[0], out var userId))
            {
                logger.LogWarning("Completion notify ignored: reason=invalid_user");
                return;
            }

            if (!int.TryParse(parts[1], out var sessionId))
            {
                logger.LogWarning("Completion notify ignored: reason=invalid_session");
                return;
            }

            if (!int.TryParse(parts[2], out var done) || !int.TryParse(parts[3], out var total) || total == 0)
            {
                return;
            }

            var projectName = parts.Length > 4 ? parts[4] : null;
            var prefix = string.IsNullOrEmpty(projectName) ? "" : $"{projectName} — ";
            var failed = total - done;
            var durationPrefix = await GetDurationPrefixAsync(sessionId);

            var summary = new StringBuilder();

            _=failed == 0
                ? summary.Append($"✅ {prefix}{durationPrefix}сессия завершена — все {done} файлов обработано")
                : done == 0
                    ? summary.Append($"❌ {prefix}{durationPrefix}сессия завершена — все {failed} файлов с ошибками")
                    : summary.Append($"⚠️ {prefix}{durationPrefix}сессия завершена: {done} ✅, {failed} ❌ из {total}");

            // Если есть ошибки — запрашиваем список файлов с ошибками
            if (failed > 0)
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

            _=await telegramOutput.SendMessageAsync(userId, summary.ToString());
            logger.LogInformation("Session completed: user={UserId}, project={Project}, done={Done}, failed={Failed}, total={Total}",
                userId, projectName, done, failed, total);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process command_completed notification");
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
