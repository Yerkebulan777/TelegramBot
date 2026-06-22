using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;

namespace TelegramBot.Data;

/// <summary>
/// Service for session data persistence and notifications.
/// </summary>
public sealed class SessionDataService(
    IConfiguration configuration,
    ILogger<SessionDataService> logger)
    : DataAccessBase(configuration.GetConnectionString("Postgres") ?? DefaultConnectionString, logger)
{
    /// <summary>Создаёт сессию с командами в одной транзакции.</summary>
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

    /// <summary>Возвращает список всех сессий.</summary>
    public async Task<List<SessionsList>> GetSessionsListAsync()
    {
        await using var conn = await CreateOpenConnectionAsync();
        var result = await conn.QueryAsync<SessionsList>(SqlQueries.Sessions.GetList);
        return result.ToList();
    }

    /// <summary>Возвращает отфильтрованный список сессий.</summary>
    public async Task<List<SessionsList>> GetSessionsListFilteredAsync(string filter)
    {
        await using var conn = await CreateOpenConnectionAsync();
        var result = await conn.QueryAsync<SessionsList>(
            SqlQueries.Sessions.GetListFiltered,
            new { Filter = filter });
        return result.ToList();
    }

    /// <summary>Считает количество сессий по фильтру (для счётчика «(всего N)» в заголовке /status).</summary>
    public async Task<int> CountSessionsFilteredAsync(string filter)
    {
        await using var conn = await CreateOpenConnectionAsync();
        return await conn.QuerySingleAsync<int>(
            SqlQueries.Sessions.CountFiltered,
            new { Filter = filter });
    }

    /// <summary>Считает очередь файлов пользователя с момента since.</summary>
    public async Task<int> CountQueuedFilesByUserSinceAsync(long userId, DateTime sinceUtc)
    {
        await using var conn = await CreateOpenConnectionAsync();
        return await conn.QuerySingleAsync<int>(
            SqlQueries.Sessions.CountQueuedFilesByUserSince,
            new { UserId = userId, SinceUtc = sinceUtc });
    }

    /// <summary>Возвращает имя пользователя по ID сессии.</summary>
    public async Task<string?> GetSessionUsernameAsync(int sessionId)
    {
        await using var conn = await CreateOpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT Username FROM Sessions WHERE SessionId = @SessionId",
            new { SessionId = sessionId });
    }

    /// <summary>Возвращает статус сессии.</summary>
    public async Task<SessionStatus> GetSessionsStatusAsync(int sessionId)
    {
        await using var conn = await CreateOpenConnectionAsync();
        var result = await conn.QuerySingleOrDefaultAsync<SessionStatus>(
            SqlQueries.Sessions.GetStatus, new { SessionId = sessionId });
        return result ?? throw new KeyNotFoundException($"Session {sessionId} not found");
    }

    /// <summary>Возвращает готовую сводку завершения сессии для уведомления.</summary>
    public async Task<SessionCompletionSummary> GetSessionCompletionSummaryAsync(int sessionId)
    {
        await using var conn = await CreateOpenConnectionAsync();

        var summary = await conn.QuerySingleOrDefaultAsync<SessionCompletionSummary>(
            SqlQueries.Sessions.GetCompletionSummary,
            new { SessionId = sessionId })
            ?? throw new KeyNotFoundException($"Session {sessionId} not found");

        var failedFiles = await conn.QueryAsync<string>(
            SqlQueries.Commands.GetFailedFilePathsBySession,
            new { SessionId = sessionId });

        summary.FailedFilePaths = failedFiles.ToList();
        return summary;
    }

    /// <summary>Возвращает команды сессии.</summary>
    public async Task<List<SessionCommands>> GetSessionsCommandsAsync(int sessionId)
    {
        await using var conn = await CreateOpenConnectionAsync();
        var result = await conn.QueryAsync<SessionCommands>(
            SqlQueries.Commands.GetBySession, new { SessionId = sessionId });
        return result.ToList();
    }

    /// <summary>Мягкое удаление сессии.</summary>
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

    /// <summary>Проверяет, есть ли активные команды в сессии.</summary>
    public async Task<bool> CheckCommandsStatusAsync(int sessionId)
    {
        await using var conn = await CreateOpenConnectionAsync();
        var count = await conn.ExecuteScalarAsync<int>(
            SqlQueries.Commands.CountActive, new { SessionId = sessionId });
        return count > 0;
    }

    /// <summary>Считает pending/processing команды в сессии.</summary>
    public async Task<int> CountPendingProcessingBySessionAsync(int sessionId)
    {
        await using var conn = await CreateOpenConnectionAsync();
        return await conn.ExecuteScalarAsync<int>(
            SqlQueries.Commands.CountPendingProcessingBySession,
            new { SessionId = sessionId });
    }

    /// <summary>Возвращает SessionId по CommandId.</summary>
    public async Task<int?> GetSessionIdByCommandAsync(int commandId, long userId, bool isAdmin = false)
    {
        await using var conn = await CreateOpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<int?>(
            SqlQueries.Commands.GetSessionIdByCommandId, new { CommandId = commandId, UserId = userId, IsAdmin = isAdmin });
    }

    /// <summary>Мягкое удаление неактивных сессий старше cutoff.</summary>
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

    /// <summary>Атомарно отправляет сигнал о завершении сессии не более одного раза.</summary>
    public async Task<bool> NotifySessionCompletedOnceAsync(int sessionId, string correlationId)
    {
        try
        {
            var payload = $"{sessionId}|{correlationId}";
            await using var conn = await CreateOpenConnectionAsync();
            var notified = await conn.ExecuteScalarAsync<int>(
                SqlQueries.Sessions.NotifyCompletionOnce,
                new { SessionId = sessionId, CorrelationId = correlationId, Payload = payload });
            return notified > 0;
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "Failed to send command_completed NOTIFY for session {SessionId}, correlationId={CorrelationId}",
                sessionId, correlationId);
            return false;
        }
    }
}
