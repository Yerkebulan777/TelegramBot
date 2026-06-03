#nullable enable

using Dapper;
using Npgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;

namespace TelegramBot.Data;

public class PostgresDataService(IConfiguration configuration, ILogger<PostgresDataService> logger) : IDataService
{
    private readonly string _connectionString = configuration.GetConnectionString("Postgres")
        ?? "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres";

    public async Task InitializeDatabaseAsync()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await conn.ExecuteAsync(SqlQueries.Schema.CreateBotUsersTable);
        await conn.ExecuteAsync(SqlQueries.Schema.CreateSessionsTable);
        await conn.ExecuteAsync(SqlQueries.Schema.CreateCommandsTable);
        await conn.ExecuteAsync(SqlQueries.Schema.CreateTrackedMessagesTable);
        await conn.ExecuteAsync(SqlQueries.Schema.CreateIndexes);
    }

    public async Task<BotUser?> GetUserAsync(long userId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        return await conn.QuerySingleOrDefaultAsync<BotUser>(SqlQueries.Users.GetById, new { UserId = userId });
    }

    public async Task UpsertUserAsync(BotUser user)
    {
        var now = DateTime.UtcNow;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
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
        int filesAmount)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var sessionId = await conn.QuerySingleAsync<long>(
            SqlQueries.Sessions.Insert,
            new { UserId = userId, Username = username, FilesAmount = filesAmount },
            tx);

        var order = 1;
        foreach (var command in commandText)
            foreach (var file in files)
                await conn.ExecuteAsync(SqlQueries.Commands.Insert,
                    new { SessionId = sessionId, CommandText = command, FilePath = file, Order = order++ },
                    tx);

        await tx.CommitAsync();
        return sessionId;
    }

    public async Task<List<SessionsList>> GetSessionsListAsync(long userId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        var result = await conn.QueryAsync<SessionsList>(SqlQueries.Sessions.GetList, new { UserId = userId });
        return result.ToList();
    }

    public async Task<SessionStatus> GetSessionsStatusAsync(int sessionId, long userId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        var result = await conn.QuerySingleOrDefaultAsync<SessionStatus>(
            SqlQueries.Sessions.GetStatus, new { SessionId = sessionId, UserId = userId });
        return result ?? throw new KeyNotFoundException($"Session {sessionId} not found");
    }

    public async Task<List<SessionCommands>> GetSessionsCommandsAsync(int sessionId, long userId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        var result = await conn.QueryAsync<SessionCommands>(
            SqlQueries.Commands.GetBySession, new { SessionId = sessionId, UserId = userId });
        return result.ToList();
    }

    public async Task<bool> DeleteSessionAsync(int sessionId, long userId)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
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

    public async Task<bool> DeleteCommandAsync(int commandId, long userId)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            var affected = await conn.ExecuteAsync(
                SqlQueries.Commands.SoftDelete, new { CommandId = commandId, UserId = userId });

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

    public async Task<bool> CheckCommandsStatusAsync(int sessionId, long userId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        var count = await conn.ExecuteScalarAsync<int>(
            SqlQueries.Commands.CountActive, new { SessionId = sessionId, UserId = userId });
        return count > 0;
    }

    public async Task<int?> GetSessionIdByCommandAsync(int commandId, long userId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        return await conn.QuerySingleOrDefaultAsync<int?>(
            SqlQueries.Commands.GetSessionIdByCommandId, new { CommandId = commandId, UserId = userId });
    }

    public async Task CreateAccessRequestAsync(long userId, string? username, string? firstName, string? lastName)
    {
        var now = DateTime.UtcNow;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(SqlQueries.Users.InsertIgnoreConflict, new
        {
            UserId = userId,
            Username = username,
            Role = (int)UserRole.User,
            Status = (int)UserAccessStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    public async Task<bool> IsUserApprovedAsync(long userId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        var status = await conn.QuerySingleOrDefaultAsync<int?>(
            SqlQueries.Users.GetStatusById, new { UserId = userId });
        return status == (int)UserAccessStatus.Approved;
    }

    public async Task<bool> ApproveUserAsync(long userId, long approvedBy)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        var rows = await conn.ExecuteAsync(SqlQueries.Users.UpdateStatus, new
        {
            UserId = userId,
            Status = (int)UserAccessStatus.Approved,
            UpdatedAt = DateTime.UtcNow
        });
        return rows > 0;
    }

    public async Task EnsureAdminUserAsync(long userId, string? username)
    {
        var now = DateTime.UtcNow;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(SqlQueries.Users.UpsertAdmin, new
        {
            UserId = userId,
            Username = username,
            Role = (int)UserRole.Admin,
            Status = (int)UserAccessStatus.Approved,
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    public async Task SaveTrackedMessagesAsync(long userId, IEnumerable<int> messageIds)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        foreach (var messageId in messageIds)
            await conn.ExecuteAsync(SqlQueries.TrackedMessages.Insert, new { UserId = userId, MessageId = messageId }, tx);
        await tx.CommitAsync();
    }

    public async Task<ILookup<long, int>> GetAllTrackedMessagesAsync()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        var rows = await conn.QueryAsync<(long UserId, int MessageId)>(SqlQueries.TrackedMessages.GetAll);
        return rows.ToLookup(r => r.UserId, r => r.MessageId);
    }

    public async Task DeleteTrackedMessagesAsync(long userId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(SqlQueries.TrackedMessages.DeleteByUser, new { UserId = userId });
    }

    public async Task<IReadOnlyList<PendingCommand>> ClaimPendingCommandsAsync(int limit = 50)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var leaseExpiry = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();

        var result = await conn.QueryAsync<PendingCommand>(
            SqlQueries.Commands.ClaimAndReturn,
            new { Limit = limit, LeaseExpiry = leaseExpiry },
            tx);

        await tx.CommitAsync();
        return result.ToList().AsReadOnly();
    }

    public async Task ReleaseExpiredLeasesAsync()
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            var released = await conn.ExecuteAsync(
                SqlQueries.Commands.ReleaseExpiredLeases,
                new { CurrentTimeSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });

            if (released > 0)
                logger.LogInformation("Released {Count} expired leases", released);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to release expired leases");
        }
    }

    public async Task<bool> UpdateCommandStatusAsync(int commandId, string status)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            var affected = await conn.ExecuteAsync(SqlQueries.Commands.UpdateStatus,
                new { CommandId = commandId, Status = status });
            return affected > 0;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to update status for command {CommandId}", commandId);
            return false;
        }
    }

    public async Task NotifyNewCommandsAsync(int sessionId)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            await conn.ExecuteAsync("NOTIFY new_command, @SessionId", new { SessionId = sessionId });
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to send NOTIFY for session {SessionId}", sessionId);
        }
    }
}
