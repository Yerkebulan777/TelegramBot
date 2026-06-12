using Dapper;
using Npgsql;
using System.Collections.Concurrent;
using System.Threading.Channels;
using TelegramBot.Data;

namespace TelegramBot.Server.Services.Infrastructure.Telegram;

/// <summary>
/// Background service: слушает PostgreSQL LISTEN/NOTIFY на каналах 'command_completed' и 'session_started'.
/// При получении уведомления ставит задачу отправки в очередь.
/// </summary>
public sealed class CommandNotificationService(
    IConfiguration configuration,
    Channel<NotificationItem> notificationChannel,
    ILogger<CommandNotificationService> logger) : BackgroundService
{
    private readonly string _connectionString = configuration.GetConnectionString("Postgres")
        ?? DataAccessBase.DefaultConnectionString;

    // Dedup: одно уведомление о старте на сессию; очищается при завершении
    private readonly ConcurrentDictionary<int, byte> _startedSessions = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Command notifications starting");

        await PostgresReconnectLoop.RunAsync(
            "Command notifications",
            RunListenerLoopAsync,
            logger,
            stoppingToken: stoppingToken);

        logger.LogInformation("Command notifications stopped");
    }

    private async Task RunListenerLoopAsync(CancellationToken stoppingToken)
    {
        await using var conn = await NpgsqlHelper.CreateOpenConnectionAsync(_connectionString, stoppingToken);

        _=await conn.ExecuteAsync("LISTEN command_completed; LISTEN session_started;");
        conn.Notification += OnNotificationReceived;

        logger.LogInformation("Command notifications listening: channels=command_completed,session_started");

        try
        {
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
        finally
        {
            conn.Notification -= OnNotificationReceived;
        }
    }

    private void OnNotificationReceived(object sender, NpgsqlNotificationEventArgs e)
    {
        try
        {
            if (e.Payload == null) return;

            if (e.Channel == "session_started")
            {
                OnSessionStarted(e.Payload);
                return;
            }

            // command_completed — Payload: SessionId|CorrelationId
            var parts = e.Payload.Split('|', 2);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var sessionId) || string.IsNullOrWhiteSpace(parts[1]))
            {
                logger.LogWarning("Completion notify ignored: reason=invalid_payload");
                return;
            }

            _startedSessions.TryRemove(sessionId, out _);

            var item = new NotificationItem(sessionId, parts[1]);
            if (!notificationChannel.Writer.TryWrite(item))
            {
                logger.LogWarning("Completion notify dropped: reason=channel_full, session={SessionId}", sessionId);
                return;
            }

            logger.LogDebug("Completion notify queued: session={SessionId}, correlationId={CorrelationId}", sessionId, parts[1]);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to enqueue notification");
        }
    }

    private void OnSessionStarted(string payload)
    {
        // Payload: SessionId|CorrelationId|UserId
        var parts = payload.Split('|', 3);
        if (parts.Length != 3 || !int.TryParse(parts[0], out var sessionId) || !long.TryParse(parts[2], out var userId))
        {
            logger.LogWarning("Session started notify ignored: reason=invalid_payload");
            return;
        }

        if (!_startedSessions.TryAdd(sessionId, 0))
        {
            return; // уже отправляли для этой сессии
        }

        var item = new NotificationItem(sessionId, parts[1], userId);
        if (!notificationChannel.Writer.TryWrite(item))
        {
            _startedSessions.TryRemove(sessionId, out _);
            logger.LogWarning("Started notify dropped: reason=channel_full, session={SessionId}", sessionId);
            return;
        }

        logger.LogDebug("Started notify queued: session={SessionId}, correlationId={CorrelationId}", sessionId, parts[1]);
    }
}
