using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;

namespace TelegramBot.Data;

/// <summary>
/// Service for user data persistence.
/// </summary>
public sealed class UserDataService(
    IConfiguration configuration,
    ILogger<UserDataService> logger)
    : DataAccessBase(configuration.GetConnectionString("Postgres") ?? DefaultConnectionString, logger), IUserDataService
{
    /// <inheritdoc/>
    public async Task<BotUser?> GetUserAsync(long userId)
    {
        await using var conn = await CreateOpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<BotUser>(SqlQueries.Users.GetById, new { UserId = userId });
    }

    /// <inheritdoc/>
    public async Task UpsertUserAsync(BotUser user)
    {
        var now = DateTime.UtcNow;
        await using var conn = await CreateOpenConnectionAsync();
        _ = await conn.ExecuteAsync(SqlQueries.Users.Upsert, new
        {
            user.UserId,
            user.Username,
            Role = (int)user.Role,
            Status = (int)user.Status,
            CreatedAt = user.CreatedAt == default ? now : user.CreatedAt,
            UpdatedAt = user.UpdatedAt == default ? now : user.UpdatedAt
        });
    }

    /// <inheritdoc/>
    public async Task UpsertUsersBatchAsync(long[] userIds, int role, int status)
    {
        if (userIds.Length == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        await using var conn = await CreateOpenConnectionAsync();
        _ = await conn.ExecuteAsync(SqlQueries.Users.UpsertBatch, new
        {
            UserIds = userIds,
            Role = role,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now
        });
    }
}
