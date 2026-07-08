using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using TelegramBot.Data.Models;

namespace TelegramBot.Data;

public sealed class NotificationOutboxDataService(
    IConfiguration configuration,
    ILogger<NotificationOutboxDataService> logger)
    : DataAccessBase(configuration.GetConnectionString("Postgres") ?? DefaultConnectionString, logger)
{
    public const string SessionCompletedEvent = "session_completed";

    // namespace: telegram_bot_outbox_sender — mutual exclusion между репликами Server.
    // Не конфликтует с 1_234_567 (lease cleanup) и 1_234_568 (partition claim).
    private const int SenderAdvisoryLockId = 1_234_569;

    public async Task<IReadOnlyList<NotificationOutboxItem>> ClaimPendingAsync(
        string eventType,
        int limit,
        TimeSpan leaseDuration)
    {
        await using var conn = await CreateOpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var items = await conn.QueryAsync<NotificationOutboxItem>(
            SqlQueries.NotificationOutbox.ClaimPending,
            new
            {
                EventType = eventType,
                Limit = limit,
                LeaseSeconds = (int)leaseDuration.TotalSeconds
            },
            tx);

        await tx.CommitAsync();
        return items.ToList().AsReadOnly();
    }

    public async Task MarkSentAsync(long outboxId)
    {
        await using var conn = await CreateOpenConnectionAsync();
        var affected = await conn.ExecuteAsync(
            SqlQueries.NotificationOutbox.MarkSent,
            new { OutboxId = outboxId });

        if (affected == 0)
        {
            Logger.LogWarning("Outbox item was not marked sent: outboxId={OutboxId}", outboxId);
        }
    }

    public async Task MarkFailedAsync(long outboxId, int attempts, Exception exception)
    {
        var retryDelaySeconds = Math.Min(300, Math.Max(5, attempts * 10));
        var error = exception.Message.Length <= 2000
            ? exception.Message
            : exception.Message[..2000];

        await using var conn = await CreateOpenConnectionAsync();
        _ = await conn.ExecuteAsync(
            SqlQueries.NotificationOutbox.MarkFailed,
            new
            {
                OutboxId = outboxId,
                RetryDelaySeconds = retryDelaySeconds,
                LastError = error
            });
    }

    /// <summary>
    /// Пытается захватить session-level advisory lock для single-writer mutual exclusion между
    /// репликами Server. При успехе возвращает держатель, который удерживает соединение (и lock)
    /// до dispose. При неудаче (другая реплика владеет) возвращает <c>null</c>.
    /// Используется <c>NotificationSenderService</c> для drain-цикла outbox.
    /// </summary>
    public async Task<SenderLockHolder?> TryAcquireSenderLockAsync()
    {
        NpgsqlConnection? conn = null;
        try
        {
            conn = await CreateOpenConnectionAsync();
            var locked = await conn.QuerySingleAsync<bool>(
                SqlQueries.NotificationOutbox.TryAcquireSenderLock,
                new { LockId = SenderAdvisoryLockId });

            if (!locked)
            {
                await conn.DisposeAsync();
                return null;
            }

            return new SenderLockHolder(conn, SenderAdvisoryLockId, Logger);
        }
        catch (Exception ex)
        {
            if (conn is not null)
            {
                await SafeDisposeConnectionAsync(conn);
            }
            Logger.LogWarning(ex, "Failed to acquire sender advisory lock");
            return null;
        }
    }

    private static async Task SafeDisposeConnectionAsync(NpgsqlConnection conn)
    {
        try { await conn.DisposeAsync(); }
        catch { /* best-effort cleanup */ }
    }
}

/// <summary>
/// Держатель session-level advisory lock. Удерживает соединение открытым до dispose,
/// после чего освобождает lock и закрывает соединение. Используется через <c>await using</c>.
/// </summary>
public sealed class SenderLockHolder : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly int _lockId;
    private readonly ILogger _logger;

    internal SenderLockHolder(NpgsqlConnection connection, int lockId, ILogger logger)
    {
        _connection = connection;
        _lockId = lockId;
        _logger = logger;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _ = await _connection.ExecuteAsync(
                SqlQueries.NotificationOutbox.ReleaseSenderLock,
                new { LockId = _lockId });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Release sender lock fail");
        }
        finally
        {
            await _connection.DisposeAsync();
        }
    }
}
