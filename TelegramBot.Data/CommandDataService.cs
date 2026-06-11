using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using TelegramBot.Core.Models;

namespace TelegramBot.Data;

/// <summary>
/// Service for command data persistence.
/// </summary>
public sealed class CommandDataService(
    IConfiguration configuration,
    ILogger<CommandDataService> logger)
    : DataAccessBase(configuration.GetConnectionString("Postgres") ?? DefaultConnectionString, logger)
{
    private const int AdvisoryLockId = 1_234_567; // namespace: telegram_bot_lease_cleanup

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

    /// <summary>Освобождает истёкшие leases.</summary>
    public async Task ReleaseExpiredLeasesAsync()
    {
        _ = await TryExecuteWithAdvisoryLockAsync(
            "release expired leases",
            conn => conn.ExecuteAsync(
                SqlQueries.Commands.ReleaseExpiredLeases,
                new { CurrentTimeSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }));
    }

    /// <summary>Обновляет статус команды.</summary>
    public async Task<bool> UpdateCommandStatusAsync(int commandId, string status, int? processId = null, string? errorMessage = null, int? progress = null, string? result = null)
    {
        return await TryExecuteAsync(commandId, SqlQueries.Commands.UpdateStatus,
            new { CommandId = commandId, Status = status, ProcessId = processId, ErrorMessage = errorMessage, Progress = progress, Result = result },
            "update status");
    }

    /// <summary>Обновляет прогресс команды.</summary>
    public Task<bool> UpdateCommandProgressAsync(int commandId, int progress)
    {
        return TryExecuteAsync(commandId, SqlQueries.Commands.UpdateProgress,
            new { CommandId = commandId, Progress = progress },
            "update progress");
    }

    /// <summary>Обновляет результат команды.</summary>
    public Task<bool> UpdateCommandResultAsync(int commandId, string result)
    {
        return TryExecuteAsync(commandId, SqlQueries.Commands.UpdateResult,
            new { CommandId = commandId, Result = result },
            "update result");
    }

    /// <summary>Планирует повторную попытку.</summary>
    public async Task<int> ScheduleRetryAsync(int commandId, DateTime nextRetryAt, string errorMessage)
    {
        await using var conn = await CreateOpenConnectionAsync();
        var retryCount = await conn.QuerySingleAsync<int>(
            SqlQueries.Commands.ScheduleRetry,
            new { CommandId = commandId, NextRetryAt = nextRetryAt, ErrorMessage = errorMessage });
        return retryCount;
    }

    /// <summary>Возвращает команду по ID.</summary>
    public async Task<PendingCommand?> GetCommandByIdAsync(int commandId, long userId, bool isAdmin = false)
    {
        await using var conn = await CreateOpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<PendingCommand>(
            SqlQueries.Commands.GetById,
            new { CommandId = commandId, UserId = userId, IsAdmin = isAdmin });
    }

    /// <summary>Мягкое удаление команды.</summary>
    public async Task<bool> DeleteCommandAsync(int commandId, long userId, bool isAdmin = false)
    {
        try
        {
            await using var conn = await CreateOpenConnectionAsync();
            var affected = await conn.ExecuteAsync(
                SqlQueries.Commands.SoftDelete, new { CommandId = commandId, UserId = userId, IsAdmin = isAdmin });

            if (affected == 0)
            {
                Logger.LogWarning("Attempt to delete foreign or missing command {CommandId} by user {UserId}", commandId, userId);
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

    /// <summary>Мягкое удаление команд по типу.</summary>
    public async Task<int> DeleteCommandsByTypeAsync(int sessionId, string commandType)
    {
        try
        {
            await using var conn = await CreateOpenConnectionAsync();
            return await conn.ExecuteAsync(
                SqlQueries.Commands.SoftDeleteBySessionAndType, new { SessionId = sessionId, CommandType = commandType });
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Failed to delete commands of type {CommandType} in session {SessionId}", commandType, sessionId);
            return 0;
        }
    }

    /// <summary>Проверяет наличие дубликатов команд.</summary>
    public async Task<bool> HasDuplicateCommandsAsync(
        IEnumerable<string> commandTexts,
        IEnumerable<string> filePaths)
    {
        await using var conn = await CreateOpenConnectionAsync();
        var count = await conn.QuerySingleAsync<int>(
            SqlQueries.Commands.CountDuplicatePairs,
            new { CommandTexts = commandTexts.ToArray(), FilePaths = filePaths.ToArray() });
        return count > 0;
    }

    /// <summary>Выполняет SQL-команду с обработкой ошибок и возвратом признака успеха.</summary>
    private async Task<bool> TryExecuteAsync(int commandId, string sql, object parameters, string operation)
    {
        try
        {
            await using var conn = await CreateOpenConnectionAsync();
            var affected = await conn.ExecuteAsync(sql, parameters);
            return affected > 0;
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Failed to {Operation} for command {CommandId}", operation, commandId);
            return false;
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
                SqlQueries.Commands.TryAdvisoryLock, new { LockId = AdvisoryLockId });

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
                _ = await conn.ExecuteAsync(SqlQueries.Commands.ReleaseAdvisoryLock, new { LockId = AdvisoryLockId });
            }
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "Failed to {Description}", description);
        }

        return true;
    }
}
