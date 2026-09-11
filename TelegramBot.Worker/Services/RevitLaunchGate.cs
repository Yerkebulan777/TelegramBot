using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Npgsql;
using TelegramBot.Data;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Сериализует запуск Revit между всеми Worker через PostgreSQL и выдерживает
/// не менее 15 секунд между успешными вызовами Process.Start().
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
            await AcquireLockAsync(connection, ct);
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
            // После успешного запуска отсчитываем 15 секунд от фактического старта процесса.
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
            // Waiting for another Worker is an expected part of the gate.  A database/connectivity
            // failure here means Revit was not started, so it must go through ProcessRunner's retry
            // policy rather than leaving the claimed command in processing until its lease expires.
            throw new RevitLaunchException(commandId, ex);
        }
        finally
        {
            if (lockAcquired)
            {
                try
                {
                    await ReleaseLockAsync(connection, CancellationToken.None);
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
            "SELECT GREATEST(INTERVAL '0 seconds', LastLaunchAt + INTERVAL '15 seconds' - clock_timestamp()) " +
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

    private static async Task AcquireLockAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_lock($1);", connection)
        {
            // pg_advisory_lock intentionally waits while another Worker is starting Revit. Npgsql's
            // default 30-second command timeout was shorter than the legitimate queue wait and caused
            // the command to become stuck in processing before Process.Start() was reached.
            CommandTimeout = 0,
        };
        _ = command.Parameters.AddWithValue(AdvisoryLockId);
        _ = await command.ExecuteScalarAsync(ct);
    }

    private static async Task ReleaseLockAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock($1);", connection);
        _ = command.Parameters.AddWithValue(AdvisoryLockId);
        _ = await command.ExecuteScalarAsync(ct);
    }
}
