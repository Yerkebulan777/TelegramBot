using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using TelegramBot.Core.Config;

namespace TelegramBot.Data;

/// <summary>
/// Инициализирует схему PostgreSQL при старте как hosted service.
/// Не блокирует старт процесса: схема создаётся в фоне с retry, потому что
/// Docker/Postgres часто ещё не слушают сразу после входа. Остальные hosted-сервисы
/// ждут <see cref="SchemaReadyGate"/> и не ходят в БД до успеха.
/// </summary>
public sealed class DatabaseInitializerService(
    IConfiguration configuration,
    IOptions<FileSystemOptions> fileSystemOptions,
    UncRootPathValidator uncRootPathValidator,
    SchemaReadyGate schemaReadyGate,
    ILogger<DatabaseInitializerService> logger)
    : BackgroundService
{
    private static readonly TimeSpan[] RetryBackoff =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await InitializeSchemaAsync(stoppingToken);
                schemaReadyGate.MarkReady();
                logger.LogInformation("Database schema initialized");
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                attempt++;
                var delay = RetryBackoff[(attempt - 1) % RetryBackoff.Length];
                logger.LogWarning(ex,
                    "Database schema init attempt {Attempt} failed, retrying in {Delay}s",
                    attempt, delay.TotalSeconds);
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task InitializeSchemaAsync(CancellationToken ct)
    {
        var hasLegacyRootPath = uncRootPathValidator.TryValidate(
            fileSystemOptions.Value.RootPath,
            out var legacyRootPath,
            out _);

        await using var conn = await DataAccessBase.OpenConnectionAsync(
            DataAccessBase.ResolveConnectionString(configuration), ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            _ = await conn.ExecuteAsync(SqlQueries.Schema.CreateSessionsTable, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.EnsureSessionsColumns, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.CreateCommandsTable, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.EnsureCommandsColumns, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.CreateTrackedMessagesTable, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.MakeTrackedMessagesSessionNullable, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.EnsureTrackedMessageLifecycle, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.CreateNotificationOutboxTable, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.EnsureNotificationOutboxColumns, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.CreateRuntimeSettingsTable, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.CreateProcessLaunchStateTable, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.CreateIndexes, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Commands.SoftDeleteLegacyCancelled, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.AddCommandsStatusCheck, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.AddSessionsStatusCheck, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.AddNotificationOutboxStatusCheck, transaction: tx);

            if (hasLegacyRootPath)
            {
                _ = await conn.ExecuteAsync(SqlQueries.RuntimeSettings.SeedRootPath, new { RootPath = legacyRootPath }, tx);
            }

            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database schema initialization failed, rolling back");
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
