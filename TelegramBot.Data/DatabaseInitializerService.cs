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
///
/// Запускается в фоне, поэтому НЕ блокирует старт хоста: сервис рапортует SCM
/// «started» немедленно, а схема создаётся с retry-циклом. Раньше это вызывалось
/// синхронно в Program.Main до host.RunAsync — пока БД (в Docker) не поднималась
/// при загрузке машины, инициализация висела дольше 60 c и SCM убивал старт
/// службы по таймауту (event 7009/7000). Сеть/Postgres у Docker поднимаются
/// позже, чем эта служба (AUTO_START delayed), поэтому retry обязателен.
/// </summary>
public sealed class DatabaseInitializerService(
    IConfiguration configuration,
    IOptions<FileSystemOptions> fileSystemOptions,
    UncRootPathValidator uncRootPathValidator,
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

        await using var conn = await CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            _ = await conn.ExecuteAsync(SqlQueries.Schema.CreateSessionsTable, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.EnsureSessionsColumns, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.CreateCommandsTable, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.EnsureCommandsColumns, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.CreateTrackedMessagesTable, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.MakeTrackedMessagesSessionNullable, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.CreateNotificationOutboxTable, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.EnsureNotificationOutboxColumns, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.CreateRuntimeSettingsTable, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.CreateIndexes, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.AddCommandsStatusCheck, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.AddSessionsStatusCheck, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Schema.AddNotificationOutboxStatusCheck, transaction: tx);
            _ = await conn.ExecuteAsync(SqlQueries.Commands.SoftDeleteLegacyCancelled, transaction: tx);

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

    private async Task<NpgsqlConnection> CreateOpenConnectionAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(DataAccessBase.ResolveConnectionString(configuration));
        await conn.OpenAsync(ct);
        return conn;
    }
}
