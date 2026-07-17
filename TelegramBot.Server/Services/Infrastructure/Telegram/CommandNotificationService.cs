using Dapper;
using Npgsql;
using System.Collections.Concurrent;
using System.Threading.Channels;
using TelegramBot.Data;
using TelegramBot.Server.Models;

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
    private readonly string _connectionString = DataAccessBase.ResolveConnectionString(configuration);

    // Dedup: одно уведомление о старте на сессию; очищается при завершении
    private readonly ConcurrentDictionary<int, byte> _startedSessions = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Cmd notifications start");

        var reconnectDelayMs = 5_000;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunListenerLoopAsync(stoppingToken);
                reconnectDelayMs = 5_000;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Cmd notify listener lost: retry={Delay}ms", reconnectDelayMs);
                try { await Task.Delay(reconnectDelayMs, stoppingToken); }
                catch (OperationCanceledException) { break; }
                reconnectDelayMs = Math.Min((int)(reconnectDelayMs * 1.5), 60_000);
            }
        }

        logger.LogInformation("Cmd notifications stop");
    }

    private async Task RunListenerLoopAsync(CancellationToken stoppingToken)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(stoppingToken);

        _=await conn.ExecuteAsync("LISTEN command_completed; LISTEN session_started;");
        conn.Notification += OnNotificationReceived;

        logger.LogInformation("Cmd notify listen: channels=command_completed,session_started");

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
                    logger.LogError(ex, "Cmd notify error: source=postgres");
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
            if (e.Payload == null)
            {
                return;
            }

            if (e.Channel == "session_started")
            {
                OnSessionStarted(e.Payload);
                return;
            }

            // command_completed — Payload: SessionId|CorrelationId
            var parts = e.Payload.Split('|', 2);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var sessionId) || string.IsNullOrWhiteSpace(parts[1]))
            {
                logger.LogWarning("Completion notify ignored: invalid_payload");
                return;
            }

            _=_startedSessions.TryRemove(sessionId, out _);

            if (!notificationChannel.Writer.TryWrite(NotificationItem.CompletionWakeUp))
            {
                logger.LogWarning("Completion wake-up dropped: channel full, session={SessionId}", sessionId);
                return;
            }

            logger.LogDebug("Completion wake-up queued: session={SessionId}, corr={CorrelationId}", sessionId, parts[1]);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Enqueue notify fail");
        }
    }

    private void OnSessionStarted(string payload)
    {
        // Payload: SessionId|CorrelationId|UserId
        var parts = payload.Split('|', 3);
        if (parts.Length != 3 || !int.TryParse(parts[0], out var sessionId) || !long.TryParse(parts[2], out var userId))
        {
            logger.LogWarning("Started notify ignored: invalid_payload");
            return;
        }

        if (!_startedSessions.TryAdd(sessionId, 0))
        {
            return; // уже отправляли для этой сессии
        }

        var item = NotificationItem.SessionStarted(sessionId, parts[1], userId);
        if (!notificationChannel.Writer.TryWrite(item))
        {
            _=_startedSessions.TryRemove(sessionId, out _);
            logger.LogWarning("Started notify dropped: channel full, session={SessionId}", sessionId);
            return;
        }

        logger.LogDebug("Started notify queued: session={SessionId}, corr={CorrelationId}", sessionId, parts[1]);
    }
}
