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
        await using var tx = await conn.BeginTransactionAsync();

        try
        {
            _ = await conn.ExecuteAsync(SqlQueries.Schema.CreateBotUsersTable, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.CreateSessionsTable, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.EnsureSessionsColumns, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.CreateCommandsTable, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.EnsureCommandsColumns, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.CreateTrackedMessagesTable, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.MakeTrackedMessagesSessionNullable, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.CreateIndexes, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Commands.SoftDeleteLegacyCancelled, transaction: tx);

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }
}
