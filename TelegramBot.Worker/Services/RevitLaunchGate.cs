using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Npgsql;
using TelegramBot.Data;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Сериализует запуск Revit между всеми Worker через PostgreSQL и выдерживает
/// не менее 30 секунд между успешными вызовами Process.Start().
/// </summary>
public sealed class RevitLaunchGate(
    IConfiguration configuration,
    ILogger<RevitLaunchGate> logger)
{
    private const long AdvisoryLockId = 1_234_569;
    private readonly string _connectionString = DataAccessBase.ResolveConnectionString(configuration);

    public async Task StartAsync(Process process, int commandId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var lockAcquired = false;

        try
        {
            await connection.OpenAsync(ct);
            await ExecuteAsync(connection, "SELECT pg_advisory_lock($1);", ct);
            lockAcquired = true;

            var remaining = await GetRemainingDelayAsync(connection, ct);
            if (remaining > TimeSpan.Zero)
            {
                logger.LogInformation(
                    "Revit launch waiting: id={CommandId}, delay={DelaySeconds}s",
                    commandId, Math.Ceiling(remaining.TotalSeconds));
                await Task.Delay(remaining, ct);
            }

            // Предварительная запись сохраняет cooldown, даже если Worker упадёт во время Process.Start().
            await SetLastLaunchAtAsync(connection, ct);
            _ = process.Start();
            // После успешного запуска отсчитываем 30 секунд от фактического старта процесса.
            try
            {
                await SetLastLaunchAtAsync(connection, CancellationToken.None);
            }
            catch (NpgsqlException ex)
            {
                // Предварительная отметка уже защищает следующий запуск; запущенный процесс не теряем.
                logger.LogWarning(ex, "Revit launch timestamp refresh failed: id={CommandId}", commandId);
            }
        }
        catch (NpgsqlException ex)
        {
            throw new CommandPersistenceException(commandId, ex);
        }
        finally
        {
            if (lockAcquired)
            {
                try
                {
                    await ExecuteAsync(connection, "SELECT pg_advisory_unlock($1);", CancellationToken.None);
                }
                catch (NpgsqlException ex)
                {
                    logger.LogWarning(ex, "Revit launch lock release failed: id={CommandId}", commandId);
                }
            }
        }
    }

    private static async Task<TimeSpan> GetRemainingDelayAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "SELECT GREATEST(INTERVAL '0 seconds', LastLaunchAt + INTERVAL '30 seconds' - clock_timestamp()) " +
            "FROM RevitLaunchState WHERE Singleton = TRUE;", connection);
        var value = await command.ExecuteScalarAsync(ct);
        return value is TimeSpan delay ? delay : TimeSpan.Zero;
    }

    private static async Task SetLastLaunchAtAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "UPDATE RevitLaunchState SET LastLaunchAt = clock_timestamp() WHERE Singleton = TRUE;", connection);
        _ = await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue(AdvisoryLockId);
        _ = await command.ExecuteScalarAsync(ct);
    }
}
