using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;

namespace TelegramBot.Data;

/// <summary>
/// Service for session data persistence and notifications.
/// </summary>
public sealed class SessionDataService(
    IConfiguration configuration,
    ILogger<SessionDataService> logger)
    : DataAccessBase(configuration.GetConnectionString("Postgres") ?? DefaultConnectionString, logger),
        ISessionDataService,
        INotificationDataService
{
    /// <inheritdoc/>
    public async Task<long> CreateSessionWithCommandsAsync(
        IEnumerable<string> commandText,
        IEnumerable<string> files,
        long userId,
        string username,
        int filesAmount,
        string? projectName = null,
        IEnumerable<int>? commandPriorities = null,
        string? correlationId = null)
    {
        var commands = commandText.ToArray();
        var fileList = files.ToArray();

        if (commands.Length == 0 || fileList.Length == 0)
        {
            throw new ArgumentException("Commands and files must not be empty");
        }

        await using var conn = await CreateOpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        correlationId ??= Guid.NewGuid().ToString("N");
        var sessionId = await conn.QuerySingleAsync<long>(
            SqlQueries.Sessions.Insert,
            new { UserId = userId, Username = username, CorrelationId = correlationId, ProjectName = projectName, FilesAmount = filesAmount },
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
                : CommandPriorities.Default;

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

        _ = await conn.ExecuteAsync(SqlQueries.Commands.InsertBatch,
            new { SessionId = sessionId, CommandTexts = commandTexts, FilePaths = filePaths, Orders = orders, Priorities = priorities },
            tx);

        // Отправляем уведомление Worker о новых задачах
        _ = await conn.ExecuteAsync("SELECT pg_notify('new_tasks', @Payload)", new { Payload = correlationId }, tx);

        await tx.CommitAsync();
        Logger.LogInformation("Session created: session={SessionId}, correlationId={CorrelationId}", sessionId, correlationId);
        return sessionId;
    }

    /// <inheritdoc/>
    public async Task<List<SessionsList>> GetSessionsListAsync()
    {
        await using var conn = await CreateOpenConnectionAsync();
        var result = await conn.QueryAsync<SessionsList>(SqlQueries.Sessions.GetList);
        return result.ToList();
    }

    /// <inheritdoc/>
    public async Task<int> CountQueuedFilesByUserSinceAsync(long userId, DateTime sinceUtc)
    {
        await using var conn = await CreateOpenConnectionAsync();
        return await conn.QuerySingleAsync<int>(
            SqlQueries.Sessions.CountQueuedFilesByUserSince,
            new { UserId = userId, SinceUtc = sinceUtc });
    }

    /// <inheritdoc/>
    public async Task<SessionStatus> GetSessionsStatusAsync(int sessionId)
    {
        await using var conn = await CreateOpenConnectionAsync();
        var result = await conn.QuerySingleOrDefaultAsync<SessionStatus>(
            SqlQueries.Sessions.GetStatus, new { SessionId = sessionId });
        return result ?? throw new KeyNotFoundException($"Session {sessionId} not found");
    }

    /// <inheritdoc/>
    public async Task<List<SessionCommands>> GetSessionsCommandsAsync(int sessionId)
    {
        await using var conn = await CreateOpenConnectionAsync();
        var result = await conn.QueryAsync<SessionCommands>(
            SqlQueries.Commands.GetBySession, new { SessionId = sessionId });
        return result.ToList();
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteSessionAsync(int sessionId, long userId, bool isAdmin = false)
    {
        try
        {
            await using var conn = await CreateOpenConnectionAsync();
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
                    Logger.LogWarning("Attempt to delete foreign or missing session {SessionId} by user {UserId}", sessionId, userId);
                    return false;
                }

                _ = await conn.ExecuteAsync(SqlQueries.Commands.SoftDeleteBySession,
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
            Logger.LogError(e, "Failed to delete session {SessionId}", sessionId);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> CheckCommandsStatusAsync(int sessionId)
    {
        await using var conn = await CreateOpenConnectionAsync();
        var count = await conn.ExecuteScalarAsync<int>(
            SqlQueries.Commands.CountActive, new { SessionId = sessionId });
        return count > 0;
    }

    /// <inheritdoc/>
    public async Task<int> CountPendingProcessingBySessionAsync(int sessionId)
    {
        await using var conn = await CreateOpenConnectionAsync();
        return await conn.ExecuteScalarAsync<int>(
            SqlQueries.Commands.CountPendingProcessingBySession,
            new { SessionId = sessionId });
    }

    /// <inheritdoc/>
    public async Task<int?> GetSessionIdByCommandAsync(int commandId, long userId, bool isAdmin = false)
    {
        await using var conn = await CreateOpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<int?>(
            SqlQueries.Commands.GetSessionIdByCommandId, new { CommandId = commandId, UserId = userId, IsAdmin = isAdmin });
    }

    /// <inheritdoc/>
    public async Task<int> SoftDeleteInactiveSessionsOlderThanAsync(DateTime cutoffUtc)
    {
        try
        {
            await using var conn = await CreateOpenConnectionAsync();
            var deletedCount = await conn.QuerySingleAsync<int>(
                SqlQueries.Sessions.SoftDeleteInactiveOlderThan,
                new { CutoffUtc = cutoffUtc });

            if (deletedCount > 0)
            {
                Logger.LogInformation("Auto-cleaned inactive sessions: count={Count}, cutoff={CutoffUtc}",
                    deletedCount, cutoffUtc);
            }

            return deletedCount;
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "Failed to auto-clean inactive sessions older than {CutoffUtc}", cutoffUtc);
            return 0;
        }
    }

    /// <inheritdoc/>
    public async Task NotifyCommandCompletedAsync(
        long userId,
        int sessionId,
        string correlationId,
        int doneCount,
        int totalCount,
        string? projectName = null)
    {
        try
        {
            var payload = $"{userId}|{sessionId}|{correlationId}|{doneCount}|{totalCount}|{projectName ?? ""}";
            await using var conn = await CreateOpenConnectionAsync();
            _ = await conn.ExecuteAsync("SELECT pg_notify('command_completed', @Payload)", new { Payload = payload });
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "Failed to send command_completed NOTIFY for session {SessionId}, correlationId={CorrelationId}",
                sessionId, correlationId);
        }
    }
}
