using Dapper;
using Npgsql;
using System.Threading.Channels;
using TelegramBot.Data;

namespace TelegramBot.Server.Services.Infrastructure.Telegram;

/// <summary>
/// Background service: слушает PostgreSQL LISTEN/NOTIFY на канале 'command_completed'.
/// При получении уведомления ставит задачу отправки в очередь.
/// </summary>
public sealed class CommandNotificationService(
    IConfiguration configuration,
    Channel<NotificationItem> notificationChannel,
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

    // VSTHRD100 suppression: event handlers must be async void — Npgsql Notification event
    // does not support async Task handlers. The try/catch inside prevents crashes.
#pragma warning disable VSTHRD100
    private async void OnNotificationReceived(object sender, NpgsqlNotificationEventArgs e)
#pragma warning restore VSTHRD100
    {
        try
        {
            if (e.Payload == null)
            {
                return;
            }

            // Payload: UserId|SessionId|CorrelationId|Done|Total|ProjectName
            var parts = e.Payload.Split('|', 6);
            if (parts.Length < 5)
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

            var correlationId = parts[2];
            if (string.IsNullOrWhiteSpace(correlationId))
            {
                logger.LogWarning("Completion notify ignored: reason=invalid_correlation_id");
                return;
            }

            if (!int.TryParse(parts[3], out var done) || !int.TryParse(parts[4], out var total) || total == 0)
            {
                return;
            }

            var projectName = parts.Length > 5 ? parts[5] : null;
            var item = new NotificationItem(userId, sessionId, correlationId, done, total, projectName);
            await notificationChannel.Writer.WriteAsync(item).AsTask();
            logger.LogDebug("Completion notify queued: session={SessionId}, correlationId={CorrelationId}",
                sessionId, correlationId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to enqueue command_completed notification");
        }
    }
}
