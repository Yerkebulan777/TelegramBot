using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using TelegramBot.Core.Constants;
using TelegramBot.Data.Models;

namespace TelegramBot.Data;

public sealed class NotificationOutboxDataService(
    IConfiguration configuration,
    ILogger<NotificationOutboxDataService> logger)
    : DataAccessBase(ResolveConnectionString(configuration), logger)
{
    public const string SessionCompletedEvent = "session_completed";
    public const string SessionStartedEvent = "session_started";

    public async Task<NotificationOutboxItem?> ClaimPendingAsync(
        SenderLockHolder lockHolder,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        // Одиночный CTE-стейтмент атомарен сам по себе — explicit-транзакция не нужна (в отличие от
        // Commands.ClaimAndReturn, где транзакция удерживает pg_try_advisory_xact_lock до commit).
        return await lockHolder.Connection.QuerySingleOrDefaultAsync<NotificationOutboxItem>(new CommandDefinition(
            SqlQueries.NotificationOutbox.ClaimPending,
            new
            {
                LeaseSeconds = (int)leaseDuration.TotalSeconds
            }, cancellationToken: cancellationToken));
    }

    public async Task MarkSentAsync(NotificationOutboxItem item, long chatId, int messageId,
        DateTime sentAt, DateTime deleteAfter, CancellationToken cancellationToken)
    {
        await using var conn = await CreateOpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        _ = await conn.ExecuteAsync(new CommandDefinition(SqlQueries.NotificationOutbox.TrackDeliveredMessage,
            new
            {
                item.SessionId,
                ChatId = chatId,
                MessageId = messageId,
                SentAt = sentAt,
                DeleteAfter = deleteAfter,
                Kind = item.EventType == SessionCompletedEvent ? TrackedMessageKinds.Completion : TrackedMessageKinds.JobStatus
            }, tx, cancellationToken: cancellationToken));
        if (item.EventType == SessionCompletedEvent)
        {
            _ = await conn.ExecuteAsync(new CommandDefinition(SqlQueries.NotificationOutbox.ExpireJobMessages,
                new { item.SessionId, JobStatusKind = TrackedMessageKinds.JobStatus }, tx, cancellationToken: cancellationToken));
        }
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            SqlQueries.NotificationOutbox.MarkSent,
            new { item.OutboxId }, tx, cancellationToken: cancellationToken));

        if (affected == 0)
        {
            throw new InvalidOperationException($"Outbox item {item.OutboxId} cannot be acknowledged in its current state.");
        }
        await tx.CommitAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(long outboxId, int retryDelaySeconds, bool permanent,
        Exception exception, CancellationToken cancellationToken)
    {
        var error = exception.Message.Length <= 2000
            ? exception.Message
            : exception.Message[..2000];

        await using var conn = await CreateOpenConnectionAsync(cancellationToken);
        _ = await conn.ExecuteAsync(new CommandDefinition(
            SqlQueries.NotificationOutbox.MarkFailed,
            new
            {
                OutboxId = outboxId,
                RetryDelaySeconds = retryDelaySeconds,
                LastError = error,
                Permanent = permanent
            }, cancellationToken: cancellationToken));
    }

    public async Task ReconcileAsync(SenderLockHolder lockHolder, CancellationToken cancellationToken)
    {
        var conn = lockHolder.Connection;
        var requeued = await conn.ExecuteAsync(new CommandDefinition(
            SqlQueries.NotificationOutbox.RequeueFailedCompletions, cancellationToken: cancellationToken));
        if (requeued > 0)
        {
            Logger.LogInformation("Requeued failed completion notifications: count={Count}", requeued);
        }

        var sessions = await conn.QueryAsync<(int SessionId, string CorrelationId)>(new CommandDefinition(
            SqlQueries.NotificationOutbox.GetMissingCompletions, cancellationToken: cancellationToken));
        var recovered = 0;
        foreach (var (sessionId, correlationId) in sessions)
        {
            await using var tx = await conn.BeginTransactionAsync(cancellationToken);
            _ = await conn.ExecuteAsync(new CommandDefinition(SqlQueries.Commands.AcquireSessionCompletionLock,
                new { SessionId = sessionId }, tx, cancellationToken: cancellationToken));
            recovered += await conn.ExecuteScalarAsync<int>(new CommandDefinition(SqlQueries.Sessions.NotifyCompletionOnce,
                new { SessionId = sessionId, CorrelationId = correlationId }, tx, cancellationToken: cancellationToken));
            await tx.CommitAsync(cancellationToken);
        }
        if (recovered > 0)
        {
            Logger.LogInformation("Recovered missing completion notifications: count={Count}", recovered);
        }
    }

    public async Task SuppressObsoleteAsync(SenderLockHolder lockHolder, CancellationToken cancellationToken)
    {
        _ = await lockHolder.Connection.ExecuteAsync(new CommandDefinition(
            SqlQueries.NotificationOutbox.SuppressObsolete, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Пытается захватить session-level advisory lock для single-writer mutual exclusion между
    /// репликами Server. При успехе возвращает держатель, который удерживает соединение (и lock)
    /// до dispose. При неудаче (другая реплика владеет) возвращает <c>null</c>.
    /// Используется <c>NotificationSenderService</c> для drain-цикла outbox.
    /// </summary>
    public async Task<SenderLockHolder?> TryAcquireSenderLockAsync(CancellationToken cancellationToken = default)
    {
        NpgsqlConnection? conn = null;
        try
        {
            conn = await CreateOpenConnectionAsync(cancellationToken);
            var locked = await conn.QuerySingleAsync<bool>(new CommandDefinition(
                SqlQueries.NotificationOutbox.TryAcquireSenderLock,
                new { LockId = AdvisoryLockIds.OutboxSender }, cancellationToken: cancellationToken));

            if (!locked)
            {
                await conn.DisposeAsync();
                return null;
            }

            return new SenderLockHolder(conn, AdvisoryLockIds.OutboxSender, Logger);
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
    internal NpgsqlConnection Connection => _connection;

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
