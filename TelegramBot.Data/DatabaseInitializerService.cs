using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramBot.Core.Interfaces;

namespace TelegramBot.Data;

/// <summary>
/// Service for database initialization.
/// </summary>
public sealed class DatabaseInitializerService(
    IConfiguration configuration,
    ILogger<DatabaseInitializerService> logger)
    : DataAccessBase(configuration.GetConnectionString("Postgres") ?? DefaultConnectionString, logger), IDatabaseInitializer
{
    /// <inheritdoc/>
    public async Task InitializeDatabaseAsync()
    {
        await using var conn = await CreateOpenConnectionAsync();

        _ = await conn.ExecuteAsync(SqlQueries.Schema.CreateBotUsersTable);
        _ = await conn.ExecuteAsync(SqlQueries.Schema.CreateSessionsTable);
        _ = await conn.ExecuteAsync(SqlQueries.Schema.EnsureSessionsColumns);
        _ = await conn.ExecuteAsync(SqlQueries.Schema.CreateCommandsTable);
        _ = await conn.ExecuteAsync(SqlQueries.Schema.EnsureCommandsColumns);
        _ = await conn.ExecuteAsync(SqlQueries.Schema.CreateTrackedMessagesTable);
        _ = await conn.ExecuteAsync(SqlQueries.Schema.MakeTrackedMessagesSessionNullable);
        _ = await conn.ExecuteAsync(SqlQueries.Schema.CreateIndexes);
        _ = await conn.ExecuteAsync(SqlQueries.Commands.SoftDeleteLegacyCancelled);
    }
}
