using System.Diagnostics;
using Dapper;
using Npgsql;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Background service: ожидает уведомления через Postgres LISTEN/NOTIFY,
/// при получении сигнала проверяет БД на наличие новых команд и выполняет их.
/// Автоматически переподключается при потере соединения.
/// </summary>
public sealed class CommandExecutionService(
    IDataService dataService,
    IConfiguration configuration,
    ILogger<CommandExecutionService> logger) : BackgroundService
{
    private const int FallbackTimeoutSec = 300; // 5 мин — safety net, если NOTIFY потерян
    private const int DefaultBatchSize = 50;
    private const int LeaseTimeoutMin = 5;
    private const int ReconnectDelayMs = 5_000; // 5 сек между попытками переподключения

    private readonly string _connectionString = configuration.GetConnectionString("Postgres")
        ?? "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Worker starting...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunListenerLoopAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Connection lost. Reconnecting in {Delay}ms...", ReconnectDelayMs);
                await Task.Delay(ReconnectDelayMs, stoppingToken);
            }
        }

        logger.LogInformation("Worker stopped");
    }

    private async Task RunListenerLoopAsync(CancellationToken stoppingToken)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(stoppingToken);

        await conn.ExecuteAsync("LISTEN new_command;");

        conn.Notification += OnNotificationReceived;

        logger.LogInformation("Connected. Listening for NOTIFY on 'new_command'...");

        // Освобождаем истёкшие Lease (crash recovery упавших воркеров)
        await dataService.ReleaseExpiredLeasesAsync();

        // Первичная проверка — вдруг команды уже есть в БД
        await ProcessBatchAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Блокирующее ожидание NOTIFY или таймаут (5 мин)
                // При NOTIFY — просыпается мгновенно
                // При таймауте — fallback poll (safety net)
                await conn.WaitAsync(TimeSpan.FromSeconds(FallbackTimeoutSec), stoppingToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (TimeoutException)
            {
                // Fallback poll — если NOTIFY был потерян
                logger.LogDebug("Fallback poll: checking for pending commands");
            }
            catch (NpgsqlException ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "PostgreSQL connection error, reconnecting...");
                throw; // Выходим из цикла → outer reconnect
            }

            // Освобождаем истёкшие Lease перед каждым циклом
            await dataService.ReleaseExpiredLeasesAsync();
            await ProcessBatchAsync(stoppingToken);
        }
    }

    private void OnNotificationReceived(object sender, NpgsqlNotificationEventArgs e)
    {
        logger.LogDebug("NOTIFY received: channel='{Channel}', payload='{Payload}'",
            e.Channel, e.Payload);
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        try
        {
            var claimed = await dataService.ClaimPendingCommandsAsync(DefaultBatchSize);

            if (claimed.Count == 0)
                return;

            logger.LogInformation("Claimed {Count} commands for processing", claimed.Count);

            foreach (var cmd in claimed)
            {
                if (ct.IsCancellationRequested)
                    break;

                await ExecuteOneAsync(cmd, ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing batch");
        }
    }

    private async Task ExecuteOneAsync(PendingCommand cmd, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            switch (cmd.CommandText)
            {
                case "PDF":
                case "DWG":
                case "IFC":
                case "BIMDOC":
                    await RunRevitCommandAsync(cmd, ct);
                    break;

                case "NWC":
                case "CLASHREP":
                    await RunNavisworksCommandAsync(cmd, ct);
                    break;

                case "AUTORES":
                    await RunAiAgentCommandAsync(cmd, ct);
                    break;

                default:
                    logger.LogWarning("Unknown command '{Cmd}' ({Id})", cmd.CommandText, cmd.CommandId);
                    await dataService.UpdateCommandStatusAsync(cmd.CommandId, CommandStatuses.Failed);
                    return;
            }

            sw.Stop();
            await dataService.UpdateCommandStatusAsync(cmd.CommandId, CommandStatuses.Done);
            logger.LogInformation("Done: {Cmd} / {File} ({Id}) — {Ms}ms",
                cmd.CommandText, cmd.FilePath, cmd.CommandId, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex, "Failed: {Cmd} / {File} ({Id})", cmd.CommandText, cmd.FilePath, cmd.CommandId);
            await dataService.UpdateCommandStatusAsync(cmd.CommandId, CommandStatuses.Failed);
        }
    }

    private Task RunRevitCommandAsync(PendingCommand cmd, CancellationToken ct)
    {
        logger.LogInformation("Revit: {Cmd} → {File}", cmd.CommandText, cmd.FilePath);
        // TODO: запуск Revit, вызов add-in / COM API
        return Task.CompletedTask;
    }

    private Task RunNavisworksCommandAsync(PendingCommand cmd, CancellationToken ct)
    {
        logger.LogInformation("Navisworks: {Cmd} → {File}", cmd.CommandText, cmd.FilePath);
        // TODO: NWC через FileConvert.exe, Clash через COM API
        return Task.CompletedTask;
    }

    private Task RunAiAgentCommandAsync(PendingCommand cmd, CancellationToken ct)
    {
        logger.LogInformation("AI Agent: {Cmd} → {File}", cmd.CommandText, cmd.FilePath);
        // TODO: HTTP-запрос к AI-агенту / запуск скрипта
        return Task.CompletedTask;
    }
}
