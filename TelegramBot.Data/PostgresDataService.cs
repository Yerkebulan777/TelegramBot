
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;

namespace TelegramBot.Data;

public class PostgresDataService(IConfiguration configuration, ILogger<PostgresDataService> logger) : IDataService
{
    private readonly string _connectionString = configuration.GetConnectionString("Postgres")
        ?? "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres";

    private async Task<NpgsqlConnection> CreateConnectionAsync()
    {
        return await NpgsqlHelper.CreateOpenConnectionAsync(_connectionString);
    }

    public async Task InitializeDatabaseAsync()
    {
        await using var conn = await CreateConnectionAsync();

        await conn.ExecuteAsync(SqlQueries.Schema.CreateBotUsersTable);
        await conn.ExecuteAsync(SqlQueries.Schema.CreateSessionsTable);
        await conn.ExecuteAsync(SqlQueries.Schema.CreateCommandsTable);
        await conn.ExecuteAsync(SqlQueries.Schema.CreateIndexes);
        await conn.ExecuteAsync(SqlQueries.Commands.SoftDeleteLegacyCancelled);
    }

    public async Task<BotUser?> GetUserAsync(long userId)
    {
        await using var conn = await CreateConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<BotUser>(SqlQueries.Users.GetById, new { UserId = userId });
    }

    public async Task UpsertUserAsync(BotUser user)
    {
        var now = DateTime.UtcNow;
        await using var conn = await CreateConnectionAsync();
        await conn.ExecuteAsync(SqlQueries.Users.Upsert, new
        {
            user.UserId,
            user.Username,
            Role = (int)user.Role,
            Status = (int)user.Status,
            CreatedAt = user.CreatedAt == default ? now : user.CreatedAt,
            UpdatedAt = user.UpdatedAt == default ? now : user.UpdatedAt
        });
    }

    public async Task<long> CreateSessionWithCommandsAsync(
        IEnumerable<string> commandText,
        IEnumerable<string> files,
        long userId,
        string username,
        int filesAmount,
        string? projectName = null,
        IEnumerable<int>? commandPriorities = null)
    {
        var commands = commandText.ToArray();
        var fileList = files.ToArray();

        if (commands.Length == 0 || fileList.Length == 0)
        {
            throw new ArgumentException("Commands and files must not be empty");
        }

        await using var conn = await CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var sessionId = await conn.QuerySingleAsync<long>(
            SqlQueries.Sessions.Insert,
            new { UserId = userId, Username = username, ProjectName = projectName, FilesAmount = filesAmount },
            tx);

        var totalRows = commands.Length * fileList.Length;
        var commandTexts = new string[totalRows];
        var filePaths = new string[totalRows];
        var orders = new int[totalRows];
        var priorities = new int[totalRows];
        var priorityArray = commandPriorities?.ToArray();

        var index = 0;
        var cmdIdx = 0;
        foreach (var cmd in commands)
        {
            var priority = priorityArray != null && cmdIdx < priorityArray.Length
                ? priorityArray[cmdIdx]
                : 50;

            foreach (var file in fileList)
            {
                commandTexts[index] = cmd;
                filePaths[index] = file;
                orders[index] = index + 1;
                priorities[index] = priority;
                index++;
            }

            cmdIdx++;
        }

        await conn.ExecuteAsync(SqlQueries.Commands.InsertBatch,
            new { SessionId = sessionId, CommandTexts = commandTexts, FilePaths = filePaths, Orders = orders, Priorities = priorities },
            tx);

        await tx.CommitAsync();
        return sessionId;
    }

    public async Task<List<SessionsList>> GetSessionsListAsync()
    {
        await using var conn = await CreateConnectionAsync();
        var result = await conn.QueryAsync<SessionsList>(SqlQueries.Sessions.GetList);
        return result.ToList();
    }

    public async Task<SessionStatus> GetSessionsStatusAsync(int sessionId)
    {
        await using var conn = await CreateConnectionAsync();
        var result = await conn.QuerySingleOrDefaultAsync<SessionStatus>(
            SqlQueries.Sessions.GetStatus, new { SessionId = sessionId });
        return result ?? throw new KeyNotFoundException($"Session {sessionId} not found");
    }

    public async Task<List<SessionCommands>> GetSessionsCommandsAsync(int sessionId)
    {
        await using var conn = await CreateConnectionAsync();
        var result = await conn.QueryAsync<SessionCommands>(
            SqlQueries.Commands.GetBySession, new { SessionId = sessionId });
        return result.ToList();
    }

    public async Task<bool> DeleteSessionAsync(int sessionId, long userId, bool isAdmin = false)
    {
        try
        {
            await using var conn = await CreateConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            try
            {
                var affected = await conn.ExecuteAsync(
                    SqlQueries.Sessions.SoftDelete,
                    new { SessionId = sessionId, UserId = userId, IsAdmin = isAdmin },
                    tx);

                if (affected == 0)
                {
                    await tx.RollbackAsync();
                    logger.LogWarning("Attempt to delete foreign or missing session {SessionId} by user {UserId}", sessionId, userId);
                    return false;
                }

                await conn.ExecuteAsync(SqlQueries.Commands.SoftDeleteBySession,
                    new { SessionId = sessionId }, tx);
                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }

            return true;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to delete session {SessionId}", sessionId);
            return false;
        }
    }

    public async Task<bool> DeleteCommandAsync(int commandId, long userId, bool isAdmin = false)
    {
        try
        {
            await using var conn = await CreateConnectionAsync();
            var affected = await conn.ExecuteAsync(
                SqlQueries.Commands.SoftDelete, new { CommandId = commandId, UserId = userId, IsAdmin = isAdmin });

            if (affected == 0)
            {
                logger.LogWarning("Attempt to delete foreign or missing command {CommandId} by user {UserId}", commandId, userId);
                return false;
            }

            return true;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to delete command {CommandId}", commandId);
            return false;
        }
    }

    public async Task UpsertUsersBatchAsync(long[] userIds, int role, int status)
    {
        if (userIds.Length == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        await using var conn = await CreateConnectionAsync();
        await conn.ExecuteAsync(SqlQueries.Users.UpsertBatch, new
        {
            UserIds = userIds,
            Role = role,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    public async Task<bool> CheckCommandsStatusAsync(int sessionId)
    {
        await using var conn = await CreateConnectionAsync();
        var count = await conn.ExecuteScalarAsync<int>(
            SqlQueries.Commands.CountActive, new { SessionId = sessionId });
        return count > 0;
    }

    public async Task<int> CountPendingProcessingBySessionAsync(int sessionId)
    {
        await using var conn = await CreateConnectionAsync();
        return await conn.ExecuteScalarAsync<int>(
            SqlQueries.Commands.CountPendingProcessingBySession,
            new { SessionId = sessionId });
    }

    public async Task<int?> GetSessionIdByCommandAsync(int commandId, long userId, bool isAdmin = false)
    {
        await using var conn = await CreateConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<int?>(
            SqlQueries.Commands.GetSessionIdByCommandId, new { CommandId = commandId, UserId = userId, IsAdmin = isAdmin });
    }

    public async Task<IReadOnlyList<PendingCommand>> ClaimPendingCommandsAsync(int limit = 50, int leaseTimeoutMinutes = 5)
    {
        await using var conn = await CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var leaseExpiry = DateTimeOffset.UtcNow.AddMinutes(leaseTimeoutMinutes).ToUnixTimeSeconds();

        var result = await conn.QueryAsync<PendingCommand>(
            SqlQueries.Commands.ClaimAndReturn,
            new { Limit = limit, LeaseExpiry = leaseExpiry },
            tx);

        await tx.CommitAsync();
        return result.ToList().AsReadOnly();
    }


    private const int AdvisoryLockId = 1_234_567; // namespace: telegram_bot_lease_cleanup

    private async Task<bool> TryExecuteWithAdvisoryLockAsync(
        string description,
        Func<NpgsqlConnection, Task<int>> operation)
    {
        try
        {
            await using var conn = await CreateConnectionAsync();

            var locked = await conn.QuerySingleAsync<bool>(
                SqlQueries.Commands.TryAdvisoryLock, new { LockId = AdvisoryLockId });

            if (!locked)
            {
                logger.LogDebug("Advisory lock for '{Description}' not acquired — another worker is processing", description);
                return false;
            }

            try
            {
                var count = await operation(conn);
                if (count > 0)
                {
                    logger.LogInformation("{Description}: affected {Count}", description, count);
                }
            }
            finally
            {
                await conn.ExecuteAsync(SqlQueries.Commands.ReleaseAdvisoryLock, new { LockId = AdvisoryLockId });
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to {Description}", description);
        }

        return true;
    }

    public async Task ReleaseExpiredLeasesAsync()
    {
        await TryExecuteWithAdvisoryLockAsync(
            "release expired leases",
            conn => conn.ExecuteAsync(
                SqlQueries.Commands.ReleaseExpiredLeases,
                new { CurrentTimeSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }));
    }

    public async Task<bool> UpdateCommandStatusAsync(int commandId, string status, int? processId = null, string? errorMessage = null)
    {
        try
        {
            await using var conn = await CreateConnectionAsync();
            var affected = await conn.ExecuteAsync(SqlQueries.Commands.UpdateStatus,
                new { CommandId = commandId, Status = status, ProcessId = processId, ErrorMessage = errorMessage });
            return affected > 0;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to update status for command {CommandId}", commandId);
            return false;
        }
    }

    public async Task ReleaseTimeoutCommandsAsync(int timeoutSeconds)
    {
        await TryExecuteWithAdvisoryLockAsync(
            "release timeout commands",
            conn => conn.ExecuteAsync(
                SqlQueries.Commands.ReleaseTimeoutCommands,
                new { TimeoutSeconds = timeoutSeconds }));
    }

    public async Task NotifyCommandCompletedAsync(long userId, int sessionId, int doneCount, int totalCount, string? projectName = null)
    {
        try
        {
            var payload = $"{userId}|{sessionId}|{doneCount}|{totalCount}|{projectName ?? ""}";
            await using var conn = await CreateConnectionAsync();
            await conn.ExecuteAsync("SELECT pg_notify('command_completed', @Payload)", new { Payload = payload });
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to send command_completed NOTIFY for session {SessionId}", sessionId);
        }
    }

    public async Task<int> ScheduleRetryAsync(int commandId, DateTime nextRetryAt, string errorMessage)
    {
        await using var conn = await CreateConnectionAsync();
        var retryCount = await conn.QuerySingleAsync<int>(
            SqlQueries.Commands.ScheduleRetry,
            new { CommandId = commandId, NextRetryAt = nextRetryAt, ErrorMessage = errorMessage });
        return retryCount;
    }

    public async Task<PendingCommand?> GetCommandByIdAsync(int commandId, long userId, bool isAdmin = false)
    {
        await using var conn = await CreateConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<PendingCommand>(
            SqlQueries.Commands.GetById,
            new { CommandId = commandId, UserId = userId, IsAdmin = isAdmin });
    }

    public async Task<bool> HasDuplicateCommandsAsync(
        IEnumerable<string> commandTexts,
        IEnumerable<string> filePaths)
    {
        await using var conn = await CreateConnectionAsync();
        var count = await conn.QuerySingleAsync<int>(
            SqlQueries.Commands.CountDuplicatePairs,
            new { CommandTexts = commandTexts.ToArray(), FilePaths = filePaths.ToArray() });
        return count > 0;
    }
}
