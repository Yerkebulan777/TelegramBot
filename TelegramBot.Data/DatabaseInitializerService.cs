using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
namespace TelegramBot.Data;

/// <summary>
/// Service for database initialization.
/// </summary>
public sealed class DatabaseInitializerService(
    IConfiguration configuration,
    ILogger<DatabaseInitializerService> logger)
    : DataAccessBase(configuration.GetConnectionString("Postgres") ?? DefaultConnectionString, logger)
{
    /// <inheritdoc/>
    public async Task InitializeDatabaseAsync()
    {
        await using var conn = await OpenConnectionWithRetryAsync();
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
            _ = await conn.ExecuteAsync(SqlQueries.Schema.CreateNotificationOutboxTable, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.EnsureNotificationOutboxColumns, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.CreateIndexes, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Commands.SoftDeleteLegacyCancelled, transaction: tx);

            await tx.CommitAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Database schema initialization failed, rolling back");
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// Открывает подключение к PostgreSQL с ретраями и экспоненциальной задержкой.
    /// Сглаживает кратковременную недоступность БД при старте сервера (например, рестарт контейнера).
    /// </summary>
    private async Task<NpgsqlConnection> OpenConnectionWithRetryAsync()
    {
        int[] retryDelaysMs = [1000, 2000, 4000, 8000];

        for (var attempt = 0; attempt < retryDelaysMs.Length; attempt++)
        {
            try
            {
                return await CreateOpenConnectionAsync();
            }
            catch (NpgsqlException ex)
            {
                Logger.LogWarning(ex,
                    "Database connection attempt {Attempt}/{MaxAttempts} failed, retrying in {DelayMs}ms",
                    attempt + 1, retryDelaysMs.Length + 1, retryDelaysMs[attempt]);
                await Task.Delay(retryDelaysMs[attempt]);
            }
        }

        return await CreateOpenConnectionAsync();
    }
}
