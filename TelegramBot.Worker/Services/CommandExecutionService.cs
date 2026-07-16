using Microsoft.Extensions.Options;
using Npgsql;
using System.Collections.Concurrent;
using System.Diagnostics;
using TelegramBot.Core.Config;
using TelegramBot.Core.Models;
using TelegramBot.Data;
using TelegramBot.BimLib.Models;
using TelegramBot.BimLib.Monitor;
using TelegramBot.Worker.Helpers;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Background service: слушает PostgreSQL LISTEN/NOTIFY и триггерит <see cref="CommandOrchestrator"/>.
/// Сам claim/launch не делает — владеет только соединением, reconnect-backoff, cleanup и health-мониторингом.
/// </summary>
public sealed class CommandExecutionService(
    CommandDataService commandDataService,
    CommandOrchestrator orchestrator,
    ProcessRunner processRunner,
    IOptions<WorkerOptions> workerOptions,
    IConfiguration configuration,
    ILogger<CommandExecutionService> logger,
    DialogDismisser dialogDismisser) : BackgroundService
{
    private const string ListenChannel = "new_tasks";
    private const int ShutdownBudgetSeconds = 30;
    private const int TaskWaitTimeoutSeconds = 15;

    private readonly ConcurrentDictionary<int, DateTime> _unresponsiveSince = new();

    private readonly string _connectionString = configuration.GetConnectionString("Postgres")
        ?? DataAccessBase.DefaultConnectionString;
    private readonly WorkerOptions _workerOptions = workerOptions.Value;

    private CancellationTokenSource? _shutdownCts;
    private Task? _cleanupTask;
    private Task? _processMonitorTask;
    private Task? _drainTimerTask;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Worker start: maxC={MaxConcurrentCommands}", _workerOptions.MaxConcurrentCommands);

        _shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        _drainTimerTask = StartPeriodicBackgroundTaskAsync(
            intervalSeconds: _workerOptions.FallbackPollingIntervalSeconds,
            disabledMessage: interval => $"Drain safety-net disabled: interval={interval}s",
            cycleName: "drain safety-net",
            cycle: () => orchestrator.TriggerDrainAsync(_shutdownCts!.Token));

        _cleanupTask = StartPeriodicBackgroundTaskAsync(
            intervalSeconds: _workerOptions.CleanupIntervalSeconds,
            disabledMessage: interval => $"Cleanup disabled: interval={interval}s",
            cycleName: "lease cleanup",
            cycle: commandDataService.ReleaseExpiredLeasesAsync);

        _processMonitorTask = StartPeriodicBackgroundTaskAsync(
            intervalSeconds: RoundUpTo30Seconds(_workerOptions.ProcessMonitorIntervalSeconds),
            disabledMessage: interval => $"Process monitor disabled: interval={interval}s",
            cycleName: "process monitor",
            cycle: () => { CheckProcessesHealth(); return Task.CompletedTask; });

        try
        {
            var reconnectDelayMs = 5_000;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunListenerLoopAsync(stoppingToken);
                    reconnectDelayMs = 5_000;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Listener lost: retry={Delay}ms", reconnectDelayMs);
                    try { await Task.Delay(reconnectDelayMs, stoppingToken); }
                    catch (OperationCanceledException) { break; }
                    reconnectDelayMs = Math.Min((int)(reconnectDelayMs * 1.5), 60_000);
                }
            }
        }
        finally
        {
            await PerformGracefulShutdownAsync();
        }

        logger.LogInformation("Worker stop");
    }

    private async Task RunListenerLoopAsync(CancellationToken stoppingToken)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(stoppingToken);

        await using var cmd = new NpgsqlCommand($"LISTEN {ListenChannel};", conn);
        _ = await cmd.ExecuteNonQueryAsync(stoppingToken);

        logger.LogInformation("Listen {Channel}", ListenChannel);

        await commandDataService.ReleaseExpiredLeasesAsync();
        await orchestrator.TriggerDrainAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Таймаут тут — только liveness-check соединения; периодический drain уже покрыт
            // отдельным "drain safety-net" циклом (StartPeriodicBackgroundTaskAsync), дублировать не нужно.
            var fallbackTimeoutSec = _workerOptions.FallbackPollingIntervalSeconds;
            var notificationReceived = await WaitForNotificationAsync(conn, TimeSpan.FromSeconds(fallbackTimeoutSec), stoppingToken);

            if (notificationReceived)
            {
                logger.LogDebug("NOTIFY {Channel}", ListenChannel);
                await orchestrator.TriggerDrainAsync(stoppingToken);
            }
        }
    }

    private async Task<bool> WaitForNotificationAsync(NpgsqlConnection conn, TimeSpan timeout, CancellationToken ct)
    {
        // Используем overload WaitAsync(TimeSpan, CancellationToken) — устраняет per-call
        // аллокацию CancellationTokenSource (CreateLinkedTokenSource + CancelAfter),
        // т.к. фреймворк сам обрабатывает таймаут внутри WaitAsync.
        var notificationReceived = false;

        void OnNotification(object? sender, NpgsqlNotificationEventArgs e)
        {
            if (e.Channel == ListenChannel)
            {
                notificationReceived = true;
            }
        }

        conn.Notification += OnNotification;

        try
        {
            _=await conn.WaitAsync(timeout, ct);
            return notificationReceived;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Таймаут истёк до получения уведомления — fallback polling сработает.
            return false;
        }
        catch (OperationCanceledException)
        {
            // Shutdown — пробрасываем наверх.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error waiting for notification");
            return false;
        }
        finally
        {
            conn.Notification -= OnNotification;
        }
    }

    /// <summary>
    /// Универсальный каркас для фоновых циклов на <see cref="PeriodicTimer"/>:
    /// disabled-проверка, повтор с подавлением ошибок итерации, штатное завершение по shutdown-токену.
    /// ponytail: добавление CancellationToken в data-сервисы — отдельный PR, контракт пока не меняем.
    /// </summary>
    private Task StartPeriodicBackgroundTaskAsync(
        int intervalSeconds,
        Func<int, string> disabledMessage,
        string cycleName,
        Func<Task> cycle)
    {
        return Task.Run(async () =>
        {
            if (intervalSeconds <= 0)
            {
                logger.LogWarning("{Msg}", disabledMessage(intervalSeconds));
                return;
            }

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

            try
            {
                while (await timer.WaitForNextTickAsync(_shutdownCts!.Token))
                {
                    try
                    {
                        await cycle();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error in {Cycle}", cycleName);
                    }
                }
            }
            catch (OperationCanceledException) when (_shutdownCts?.IsCancellationRequested == true)
            {
                // штатное завершение
            }
        });
    }

    private void CheckProcessesHealth()
    {
        var thresholdSeconds = Math.Max(
            RoundUpTo30Seconds(_workerOptions.UnresponsiveThresholdSeconds),
            RoundUpTo30Seconds(_workerOptions.ProcessMonitorIntervalSeconds));

        foreach (var (commandId, process) in processRunner.ActiveProcesses)
        {
            if (process.HasExited)
            {
                _ = _unresponsiveSince.TryRemove(commandId, out _);
                continue;
            }

            try
            {
                var health = ProcessHealthHelper.CheckHealth(process, logger, $"Command#{commandId}");

                if (health.Status == RevitProcessStatus.NotResponding)
                {
                    var since = _unresponsiveSince.GetOrAdd(commandId, _ => DateTime.UtcNow);
                    var stuckFor = DateTime.UtcNow - since;
                    if (stuckFor.TotalSeconds >= thresholdSeconds)
                    {
                        logger.LogWarning(
                            "Not responding: id={Id}, pid={Pid}, stuck={StuckFor}, mem={MemoryMb}MB, dur={Duration}",
                            commandId, process.Id, stuckFor, health.MemoryMb, health.Duration);
                    }
                }
                else if (_unresponsiveSince.TryRemove(commandId, out var since))
                {
                    logger.LogInformation(
                        "Responding again: id={Id}, pid={Pid}, stuck={StuckFor}",
                        commandId, process.Id, DateTime.UtcNow - since);
                }
                try
                {
                    _ = dialogDismisser.DismissDialogsForProcess((uint)process.Id);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Dismiss dialogs fail: id={CommandId}", commandId);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Health fail: id={CommandId}", commandId);
            }
        }
    }

    private static int RoundUpTo30Seconds(int seconds) => Math.Max(30, ((seconds + 29) / 30) * 30);

    private async Task PerformGracefulShutdownAsync()
    {
        logger.LogInformation("Shutdown...");

        using var shutdownBudgetCts = new CancellationTokenSource(TimeSpan.FromSeconds(ShutdownBudgetSeconds));
        var shutdownStartedAt = DateTime.UtcNow;

        if (_shutdownCts != null)
        {
            await _shutdownCts.CancelAsync();
        }

        logger.LogInformation("Shutdown: active={Count}", processRunner.ActiveProcesses.Count(p => !p.Value.HasExited));

        // Принудительно завершаем все активные процессы параллельно в общем shutdown-бюджете.
        var processesToKill = processRunner.ActiveProcesses.ToList();
        var killTasks = processesToKill.Select(kvp => KillProcessAsync(kvp.Key, kvp.Value, shutdownBudgetCts.Token)).ToList();

        if (killTasks.Count > 0)
        {
            try
            {
                await Task.WhenAll(killTasks);
            }
            catch (OperationCanceledException) when (shutdownBudgetCts.IsCancellationRequested)
            {
                logger.LogWarning("Shutdown kill >{BudgetSeconds}s", ShutdownBudgetSeconds);
            }
        }

        int Remaining() => Math.Max(1, ShutdownBudgetSeconds - (int)(DateTime.UtcNow - shutdownStartedAt).TotalSeconds);
#pragma warning disable VSTHRD003
        await WaitForTasksAsync(_drainTimerTask, "Drain safety-net", shutdownBudgetCts.Token, Math.Min(TaskWaitTimeoutSeconds, Remaining()));
        await WaitForTasksAsync(_cleanupTask, "Cleanup task", shutdownBudgetCts.Token, Math.Min(TaskWaitTimeoutSeconds, Remaining()));
        await WaitForTasksAsync(_processMonitorTask, "Process monitor", shutdownBudgetCts.Token, Math.Min(TaskWaitTimeoutSeconds, Remaining()));
        await WaitForTasksAsync(null, "Running tasks", shutdownBudgetCts.Token, Math.Min(TaskWaitTimeoutSeconds, Remaining()));
#pragma warning restore VSTHRD003

        _shutdownCts?.Dispose();

        logger.LogInformation("Shutdown done");
    }

    private async Task KillProcessAsync(int commandId, Process process, CancellationToken shutdownToken)
    {
        try
        {
            if (!process.HasExited)
            {
                logger.LogInformation("Kill process: id={Id}, pid={Pid}",
                    commandId, process.Id);

                var exited = await ProcessKillHelper.KillAsync(
                    process, TimeSpan.FromSeconds(10), logger, commandId, shutdownToken);

                if (exited)
                {
                    logger.LogInformation("Process killed: id={Id}, pid={Pid}",
                        commandId, process.Id);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Kill error: id={Id}, pid={Pid}",
                commandId, process.Id);
        }
        finally
        {
            process.Dispose();
        }
    }

    private async Task WaitForTasksAsync(Task? single, string name, CancellationToken token, int timeoutSeconds)
    {
        if (token.IsCancellationRequested)
        {
            logger.LogWarning("{Name} skip: budget exhausted", name);
            return;
        }

        try
        {
            if (single != null)
            {
                await single.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), token);
            }
            else
            {
                var tasks = orchestrator.SnapshotRunningTasks();

                if (tasks.Length == 0)
                {
                    return;
                }

                logger.LogInformation("Wait {Timeout}s for {Count} task(s)", timeoutSeconds, tasks.Length);
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), token);
            }
        }
        catch (TimeoutException)
        {
            var unfinished = single != null
                ? !single.IsCompleted ? 1 : 0
                : orchestrator.RunningTaskCount;
            logger.LogWarning("{Name}: {Count} unfinished ({Timeout}s)", name, unfinished, timeoutSeconds);
        }
        catch (OperationCanceledException)
        {
            // штатное завершение
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Name} shutdown fail", name);
        }
    }
}
