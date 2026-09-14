using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;

namespace TelegramBot.Data;

/// <summary>
/// Service for command data persistence.
/// </summary>
public sealed class CommandDataService(
    IConfiguration configuration,
    ILogger<CommandDataService> logger)
    : DataAccessBase(ResolveConnectionString(configuration), logger)
{
    /// <summary>Забирает pending команды для выполнения.</summary>
    public async Task<IReadOnlyList<PendingCommand>> ClaimPendingCommandsAsync(int limit = 50, int leaseTimeoutMinutes = 5)
    {
        await using var conn = await CreateOpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var leaseExpiry = DateTimeOffset.UtcNow.AddMinutes(leaseTimeoutMinutes).ToUnixTimeSeconds();

        var result = await conn.QueryAsync<PendingCommand>(
            SqlQueries.Commands.ClaimAndReturn,
            new { Limit = limit, LeaseExpiry = leaseExpiry },
            tx);

        await tx.CommitAsync();
        return result.ToList().AsReadOnly();
    }

    /// <summary>
    /// Освобождает истёкшие leases. Команды, чей RetryCount после инкремента достигает
    /// <paramref name="maxRetries"/>, переводятся в 'Failed' (poison-command guard), остальные — в 'pending'.
    /// </summary>
    public async Task ReleaseExpiredLeasesAsync(int maxRetries)
    {
        _ = await TryExecuteWithAdvisoryLockAsync(
            "release expired leases",
            async conn =>
            {
                var currentTimeSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var sessions = await conn.QueryAsync<(int SessionId, string CorrelationId)>(SqlQueries.Commands.GetExpiredLeaseSessions,
                    new { CurrentTimeSec = currentTimeSec });
                var count = 0;
                foreach (var (sessionId, correlationId) in sessions)
                {
                    await using var tx = await conn.BeginTransactionAsync();
                    _ = await conn.ExecuteAsync(SqlQueries.Commands.AcquireSessionCompletionLock,
                        new { SessionId = sessionId }, tx);
                    count += await conn.ExecuteAsync(SqlQueries.Commands.ReleaseExpiredLeases,
                        new { SessionId = sessionId, CurrentTimeSec = currentTimeSec, MaxRetries = maxRetries }, tx);
                    _ = await conn.ExecuteScalarAsync<int>(SqlQueries.Sessions.NotifyCompletionOnce,
                        new { SessionId = sessionId, CorrelationId = correlationId }, tx);
                    await tx.CommitAsync();
                }
                return count;
            });
    }

    /// <summary>
    /// Atomically writes a terminal command outcome and, if it is the last active command in its
    /// session, puts exactly one completion notification into the outbox.
    /// </summary>
    public async Task<bool> CompleteCommandAndNotifyAsync(
        PendingCommand command,
        string status,
        int? processId = null,
        string? errorMessage = null)
    {
        if (status is not Statuses.Done and not Statuses.Failed)
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Only terminal command statuses can be completed.");
        }

        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await using var conn = new NpgsqlConnection(ResolveConnectionString(configuration));
                await conn.OpenAsync(timeout.Token);
                await using var transaction = await conn.BeginTransactionAsync(timeout.Token);

                // Lock before the command update. A second simultaneous completion waits here, then
                // observes the first commit when it counts active commands, so the last one cannot miss
                // the session-completed notification.
                _ = await conn.ExecuteAsync(new CommandDefinition(
                    SqlQueries.Commands.AcquireSessionCompletionLock,
                    new { command.SessionId },
                    transaction,
                    cancellationToken: timeout.Token));

                var affected = await conn.ExecuteAsync(new CommandDefinition(
                    SqlQueries.Commands.UpdateStatus,
                    new
                    {
                        CommandId = command.CommandId,
                        Status = status,
                        ProcessId = processId,
                        ErrorMessage = errorMessage,
                        ClaimedLease = command.Lease,
                    },
                    transaction,
                    cancellationToken: timeout.Token));

                if (affected == 0)
                {
                    Logger.LogWarning("Terminal command status not written: id={CommandId}, status={Status}, row removed or deleted",
                        command.CommandId, status);
                    await transaction.CommitAsync(timeout.Token);
                    return false;
                }

                var notified = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    SqlQueries.Sessions.NotifyCompletionOnce,
                    new { command.SessionId, command.CorrelationId },
                    transaction,
                    cancellationToken: timeout.Token)) > 0;

                await transaction.CommitAsync(timeout.Token);
                return notified;
            }
            catch (Exception ex) when (attempt < maxAttempts &&
                (ex is NpgsqlException { IsTransient: true } or TimeoutException or OperationCanceledException))
            {
                var delaySeconds = 1 << attempt;
                Logger.LogWarning(ex,
                    "Terminal command completion retry: id={CommandId}, status={Status}, attempt={Attempt}, delay={DelaySeconds}s",
                    command.CommandId, status, attempt, delaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            }
            catch (Exception ex)
            {
                throw new CommandPersistenceException(command.CommandId, ex);
            }
        }
    }

    /// <summary>Записывает PID запущенного процесса и один раз уведомляет Server о старте сессии.</summary>
    public async Task<bool> MarkProcessStartedAndNotifyOnceAsync(
        int commandId, int processId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = await CreateOpenConnectionAsync(cancellationToken);
            var notified = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                SqlQueries.Commands.MarkProcessStartedAndNotifyOnce,
                new
                {
                    CommandId = commandId,
                    ProcessId = processId,
                },
                cancellationToken: cancellationToken));

            return notified > 0;
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "Failed to mark process started for command {CommandId}", commandId);
            return false;
        }
    }

    /// <summary>Планирует повторную попытку, только если команда всё ещё processing с тем же lease.</summary>
    public Task<int?> ScheduleRetryAsync(int commandId, long claimedLease, DateTime nextRetryAt, string errorMessage) =>
        ReturnToPendingAsync(commandId, claimedLease, nextRetryAt, errorMessage, incrementRetry: true);

    /// <summary>
    /// Возвращает claimed команду в pending без инкремента RetryCount (graceful shutdown Worker).
    /// </summary>
    public async Task<bool> ReleaseClaimedLeaseAsync(
        int commandId, long claimedLease, DateTime nextRetryAt, string errorMessage) =>
        await ReturnToPendingAsync(commandId, claimedLease, nextRetryAt, errorMessage, incrementRetry: false) is not null;

    private async Task<int?> ReturnToPendingAsync(
        int commandId, long claimedLease, DateTime nextRetryAt, string errorMessage, bool incrementRetry)
    {
        try
        {
            await using var conn = await CreateOpenConnectionAsync();
            return await conn.QuerySingleOrDefaultAsync<int?>(
                SqlQueries.Commands.ReturnToPending,
                new
                {
                    CommandId = commandId,
                    ClaimedLease = claimedLease,
                    NextRetryAt = nextRetryAt,
                    ErrorMessage = errorMessage,
                    IncrementRetry = incrementRetry,
                });
        }
        catch (Exception ex)
        {
            throw new CommandPersistenceException(commandId, ex);
        }
    }

    /// <summary>Читает завершённую команду для создания нового задания.</summary>
    public async Task<CommandRerunSnapshot?> GetCommandRerunSnapshotAsync(int commandId, long userId)
    {
        await using var conn = await CreateOpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<CommandRerunSnapshot>(
            SqlQueries.Commands.GetRerunSnapshot,
            new { CommandId = commandId, UserId = userId });
    }

    /// <summary>Мягкое удаление команды владельца. processing не удаляется.</summary>
    public async Task<bool> DeleteCommandAsync(int commandId, long userId)
    {
        try
        {
            await using var conn = await CreateOpenConnectionAsync();
            var affected = await conn.ExecuteAsync(
                SqlQueries.Commands.SoftDelete, new { CommandId = commandId, UserId = userId });

            if (affected == 0)
            {
                Logger.LogWarning("Failed to delete command {CommandId}: not found, not owned, or processing", commandId);
                return false;
            }

            return true;
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Failed to delete command {CommandId}", commandId);
            return false;
        }
    }

    /// <summary>Мягкое удаление команд по типу у владельца сессии. processing не удаляется.</summary>
    public async Task<int> DeleteCommandsByTypeAsync(int sessionId, string commandType, long userId)
    {
        try
        {
            await using var conn = await CreateOpenConnectionAsync();
            return await conn.ExecuteAsync(
                SqlQueries.Commands.SoftDeleteBySessionAndType,
                new { SessionId = sessionId, CommandType = commandType, UserId = userId });
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Failed to delete commands of type {CommandType} in session {SessionId}", commandType, sessionId);
            return 0;
        }
    }

    private async Task<bool> TryExecuteWithAdvisoryLockAsync(
        string description,
        Func<NpgsqlConnection, Task<int>> operation)
    {
        try
        {
            await using var conn = await CreateOpenConnectionAsync();

            var locked = await conn.QuerySingleAsync<bool>(
                SqlQueries.Commands.TryAdvisoryLock, new { LockId = AdvisoryLockIds.LeaseCleanup });

            if (!locked)
            {
                Logger.LogDebug("Advisory lock for '{Description}' not acquired — another worker is processing", description);
                return false;
            }

            try
            {
                var count = await operation(conn);
                if (count > 0)
                {
                    Logger.LogInformation("{Description}: affected {Count}", description, count);
                }
            }
            finally
            {
                _ = await conn.ExecuteAsync(SqlQueries.Commands.ReleaseAdvisoryLock, new { LockId = AdvisoryLockIds.LeaseCleanup });
            }
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "Failed to {Description}", description);
            return false;
        }

        return true;
    }
}
