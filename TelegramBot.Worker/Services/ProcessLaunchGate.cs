using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Npgsql;
using TelegramBot.Core.Models;
using TelegramBot.Data;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Сериализует запуск тяжёлого BIM-процесса (Revit, AutoCAD) между всеми Worker через PostgreSQL
/// и выдерживает не менее 15 секунд между успешными вызовами Process.Start() для одного продукта.
/// </summary>
public sealed class ProcessLaunchGate(
    IConfiguration configuration,
    ILogger<ProcessLaunchGate> logger)
{
    private readonly string _connectionString = DataAccessBase.ResolveConnectionString(configuration);

    public async Task StartAsync(Process process, ProcessLaunchGateKind kind, int commandId, CancellationToken ct)
    {
        var (product, advisoryLockId) = GetGateSettings(kind);

        await using var connection = new NpgsqlConnection(_connectionString);
        var lockAcquired = false;

        try
        {
            await connection.OpenAsync(ct);
            await AcquireLockAsync(connection, advisoryLockId, ct);
            lockAcquired = true;

            var remaining = await GetRemainingDelayAsync(connection, product, ct);
            if (remaining > TimeSpan.Zero)
            {
                logger.LogInformation(
                    "{Product} launch waiting: id={CommandId}, delay={DelaySeconds}s",
                    product, commandId, Math.Ceiling(remaining.TotalSeconds));
                await Task.Delay(remaining, ct);
            }

            // Предварительная запись сохраняет cooldown, даже если Worker упадёт во время Process.Start().
            await SetLastLaunchAtAsync(connection, product, ct);
            _ = process.Start();
            // После успешного запуска отсчитываем 15 секунд от фактического старта процесса.
            try
            {
                await SetLastLaunchAtAsync(connection, product, CancellationToken.None);
            }
            catch (NpgsqlException ex)
            {
                // Предварительная отметка уже защищает следующий запуск; запущенный процесс не теряем.
                logger.LogWarning(ex, "{Product} launch timestamp refresh failed: id={CommandId}", product, commandId);
            }
        }
        catch (NpgsqlException ex)
        {
            // Waiting for another Worker is an expected part of the gate.  A database/connectivity
            // failure here means the process was not started, so it must go through ProcessRunner's
            // retry policy rather than leaving the claimed command in processing until its lease expires.
            throw new ProcessLaunchException(product, commandId, ex);
        }
        finally
        {
            if (lockAcquired)
            {
                try
                {
                    await ReleaseLockAsync(connection, advisoryLockId, CancellationToken.None);
                }
                catch (NpgsqlException ex)
                {
                    logger.LogWarning(ex, "{Product} launch lock release failed: id={CommandId}", product, commandId);
                }
            }
        }
    }

    /// <summary>Строка состояния и advisory lock id; значения не конфликтуют с lease cleanup/outbox/claim.</summary>
    private static (string Product, long AdvisoryLockId) GetGateSettings(ProcessLaunchGateKind kind) => kind switch
    {
        ProcessLaunchGateKind.Revit => ("Revit", 1_234_569),
        ProcessLaunchGateKind.AutoCad => ("AutoCad", 1_234_570),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Launch gate kind has no shared state."),
    };

    private static async Task<TimeSpan> GetRemainingDelayAsync(
        NpgsqlConnection connection, string product, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "SELECT GREATEST(INTERVAL '0 seconds', LastLaunchAt + INTERVAL '15 seconds' - clock_timestamp()) " +
            "FROM ProcessLaunchState WHERE Product = $1;", connection);
        _ = command.Parameters.AddWithValue(product);
        var value = await command.ExecuteScalarAsync(ct);
        return value is TimeSpan delay ? delay : TimeSpan.Zero;
    }

    private static async Task SetLastLaunchAtAsync(
        NpgsqlConnection connection, string product, CancellationToken ct)
    {
        // Upsert, а не UPDATE: gate работает и до первого запуска продукта, без seed-строки.
        await using var command = new NpgsqlCommand(
            "INSERT INTO ProcessLaunchState (Product, LastLaunchAt) VALUES ($1, clock_timestamp()) " +
            "ON CONFLICT (Product) DO UPDATE SET LastLaunchAt = clock_timestamp();", connection);
        _ = command.Parameters.AddWithValue(product);
        _ = await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task AcquireLockAsync(NpgsqlConnection connection, long advisoryLockId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_lock($1);", connection)
        {
            // pg_advisory_lock intentionally waits while another Worker is starting the process. Npgsql's
            // default 30-second command timeout was shorter than the legitimate queue wait and caused
            // the command to become stuck in processing before Process.Start() was reached.
            CommandTimeout = 0,
        };
        _ = command.Parameters.AddWithValue(advisoryLockId);
        _ = await command.ExecuteScalarAsync(ct);
    }

    private static async Task ReleaseLockAsync(NpgsqlConnection connection, long advisoryLockId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock($1);", connection);
        _ = command.Parameters.AddWithValue(advisoryLockId);
        _ = await command.ExecuteScalarAsync(ct);
    }
}
