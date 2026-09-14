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
    : DataAccessBase(ResolveConnectionString(configuration), logger)
{
    private sealed record InsertedRow(string CommandText, string FilePath);

    /// <summary>
    /// Создаёт сессию с командами в одной транзакции. Дубликат определяется парой
    /// (команда, файл) и отсекается точечно уникальным индексом idx_commands_active_unique
    /// через ON CONFLICT DO NOTHING — остальные пары из того же запроса всё равно встают в очередь
    /// (напр. если DWG для файла уже в очереди, а PDF для того же файла — нет, PDF всё равно queued).
    /// SessionId задан только при <see cref="SessionCreateStatus.Created"/>.
    /// Транзакция берёт user-level advisory lock и пересчитывает лимит до INSERT.
    /// </summary>
    public async Task<SessionCreateResult> CreateSessionWithCommandsAsync(
        IEnumerable<string> commandText,
        IEnumerable<string> files,
        long userId,
        string username,
        int filesAmount,
        string rootPath,
        string? projectName = null,
        IEnumerable<int>? commandPriorities = null,
        string? correlationId = null,
        int maxFilesPerUserPerDay = 0)
    {
        var commands = commandText.Distinct(StringComparer.Ordinal).ToArray();
        var fileList = files.Distinct(StringComparer.Ordinal).ToArray();

        if (commands.Length == 0 || fileList.Length == 0 || string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("Commands, files, and root path must not be empty");
        }

        await using var conn = await CreateOpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await conn.ExecuteAsync(SqlQueries.Sessions.AcquireUserQueueLock, new { UserId = userId }, tx);

        if (maxFilesPerUserPerDay > 0)
        {
            var queuedToday = await conn.QuerySingleAsync<int>(
                SqlQueries.Sessions.CountQueuedFilesByUserSince,
                new { UserId = userId, SinceUtc = DateTime.UtcNow.AddDays(-1) },
                tx);
            if (fileList.Length > maxFilesPerUserPerDay - queuedToday)
            {
                await tx.RollbackAsync();
                Logger.LogWarning(
                    "Session rejected: daily file limit for user {UserId}, queued={Queued}, requested={Requested}, limit={Limit}",
                    userId, queuedToday, fileList.Length, maxFilesPerUserPerDay);
                return SessionCreateResult.DailyLimitExceeded(queuedToday);
            }
        }

        correlationId ??= Guid.NewGuid().ToString("N");
        var sessionId = await conn.QuerySingleAsync<int>(
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

        var allPairs = commandTexts.Zip(filePaths, (c, f) => (Command: c, FilePath: f)).ToArray();

        // ON CONFLICT DO NOTHING отсекает только конфликтующие (команда, файл)-пары —
        // остальные пары из этого же запроса вставляются штатно.
        var insertedRows = (await conn.QueryAsync<InsertedRow>(SqlQueries.Commands.InsertBatch,
            new { SessionId = sessionId, CommandTexts = commandTexts, FilePaths = filePaths, RootPath = rootPath, Orders = orders, Priorities = priorities },
            tx)).ToList();

        var insertedPairs = insertedRows.Select(r => (r.CommandText, r.FilePath)).ToHashSet();
        var skipped = allPairs.Where(p => !insertedPairs.Contains(p)).ToArray();
        var conflicts = skipped.Length == 0
            ? new Dictionary<(string, string), CommandConflict>()
            : (await conn.QueryAsync<CommandConflict>(SqlQueries.Commands.GetActiveConflicts,
                new
                {
                    SessionId = sessionId,
                    CommandTexts = skipped.Select(p => p.Command).ToArray(),
                    FilePaths = skipped.Select(p => p.FilePath).ToArray()
                }, tx)).ToDictionary(c => (c.Command, c.FilePath));
        // A conflicting command can finish between INSERT and this SELECT.
        var skippedPairs = skipped.Select(p => conflicts.GetValueOrDefault(p)
            ?? new CommandConflict { Command = p.Command, FilePath = p.FilePath }).ToArray();

        if (insertedRows.Count == 0)
        {
            await tx.RollbackAsync();
            Logger.LogWarning("Session rejected: all (command, file) pairs already active for user {UserId}", userId);
            return SessionCreateResult.AllDuplicates(skippedPairs);
        }

        var queuedFileCount = insertedRows.Select(r => r.FilePath).Distinct().Count();
        if (skippedPairs.Length > 0)
        {
            _ = await conn.ExecuteAsync(SqlQueries.Sessions.UpdateFilesAmount,
                new { SessionId = sessionId, FilesAmount = queuedFileCount },
                tx);
        }

        await tx.CommitAsync();
        Logger.LogInformation("Session created: session={SessionId}, correlationId={CorrelationId}, skipped={SkippedCount}", sessionId, correlationId, skippedPairs.Length);
        return SessionCreateResult.Created(sessionId, queuedFileCount, skippedPairs);
    }

    /// <summary>Возвращает отфильтрованный список сессий владельца.</summary>
    public async Task<List<SessionsList>> GetSessionsListFilteredAsync(long userId, string filter)
    {
        await using var conn = await CreateOpenConnectionAsync();
        var result = await conn.QueryAsync<SessionsList>(
            SqlQueries.Sessions.GetListFiltered,
            new { UserId = userId, Filter = filter });
        return result.ToList();
    }

    /// <summary>Возвращает имя пользователя по ID сессии.</summary>
    public async Task<string?> GetSessionUsernameAsync(int sessionId)
    {
        await using var conn = await CreateOpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<string>(
            SqlQueries.Sessions.GetUsername,
            new { SessionId = sessionId });
    }

    /// <summary>Возвращает статус сессии владельца или null, если сессии нет / чужая / удалена.</summary>
    public async Task<SessionStatus?> GetSessionsStatusAsync(int sessionId, long userId)
    {
        await using var conn = await CreateOpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<SessionStatus>(
            SqlQueries.Sessions.GetStatus, new { SessionId = sessionId, UserId = userId });
    }

    /// <summary>Возвращает готовую сводку завершения сессии для уведомления.</summary>
    public async Task<SessionCompletionSummary> GetSessionCompletionSummaryAsync(int sessionId)
    {
        await using var conn = await CreateOpenConnectionAsync();

        var summary = await conn.QuerySingleOrDefaultAsync<SessionCompletionSummary>(
            SqlQueries.Sessions.GetCompletionSummary,
            new { SessionId = sessionId })
            ?? throw new KeyNotFoundException($"Session {sessionId} not found");

        summary.Commands = [.. await conn.QueryAsync<SessionCommandInfo>(
            SqlQueries.Commands.GetCommandsForCompletion,
            new { SessionId = sessionId })];
        return summary;
    }

    /// <summary>Возвращает команды сессии владельца.</summary>
    public async Task<List<SessionCommands>> GetSessionsCommandsAsync(int sessionId, long userId)
    {
        await using var conn = await CreateOpenConnectionAsync();
        var result = await conn.QueryAsync<SessionCommands>(
            SqlQueries.Commands.GetBySession, new { SessionId = sessionId, UserId = userId });
        return result.ToList();
    }

    /// <summary>Мягкое удаление сессии владельца. Отказ, если есть processing.</summary>
    public async Task<bool> DeleteSessionAsync(int sessionId, long userId)
    {
        try
        {
            await using var conn = await CreateOpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            try
            {
                var affected = await conn.ExecuteAsync(
                    SqlQueries.Sessions.SoftDelete,
                    new { SessionId = sessionId, UserId = userId },
                    tx);

                if (affected == 0)
                {
                    await tx.RollbackAsync();
                    Logger.LogWarning("Failed to delete session {SessionId}: not found, not owned, or processing", sessionId);
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

    /// <summary>Возвращает SessionId по CommandId, только если команда принадлежит userId.</summary>
    public async Task<int?> GetSessionIdByCommandAsync(int commandId, long userId)
    {
        await using var conn = await CreateOpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<int?>(
            SqlQueries.Commands.GetSessionIdByCommandId, new { CommandId = commandId, UserId = userId });
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

}
