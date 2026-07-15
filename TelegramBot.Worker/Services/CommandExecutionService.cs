using Microsoft.Extensions.Options;
using Npgsql;
using System.Collections.Concurrent;
using System.Diagnostics;
using TelegramBot.Core.Config;
using TelegramBot.Core.Models;
using TelegramBot.Data;
using TelegramBot.BimLib.Monitor;
using TelegramBot.Worker.Helpers;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Background service: orchestrator для событийной обработки очереди команд через PostgreSQL LISTEN/NOTIFY.
/// Делегирует выполнение специализированному ProcessRunner.
/// </summary>
public sealed class CommandExecutionService(
    CommandDataService commandDataService,
    ProcessRunner processRunner,
    IOptions<WorkerOptions> workerOptions,
    IConfiguration configuration,
    ILogger<CommandExecutionService> logger,
    DialogDismisser dialogDismisser) : BackgroundService
{
    private const string ListenChannel = "new_tasks";
    private const int DefaultBatchSize = 5;
    private const int ShutdownBudgetSeconds = 30;
    private const int TaskWaitTimeoutSeconds = 15;

    // Трекинг выполняемых задач для корректного ожидания при shutdown
    private readonly HashSet<Task> _runningTasks = [];
    private readonly ConcurrentDictionary<int, DateTime> _unresponsiveSince = new();
    private readonly object _runningTasksLock = new();
    private readonly SemaphoreSlim _drainGate = new(1, 1);
    private readonly int _maxConcurrentCommands = Math.Max(1, workerOptions.Value.MaxConcurrentCommands);

    private readonly string _connectionString = configuration.GetConnectionString("Postgres")
        ?? DataAccessBase.DefaultConnectionString;
    private readonly WorkerOptions _workerOptions = workerOptions.Value;

    private CancellationTokenSource? _shutdownCts;
    private Task? _cleanupTask;
    private Task? _processMonitorTask;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Worker start: maxC={MaxConcurrentCommands}", _maxConcurrentCommands);

        _shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        _cleanupTask = StartCleanupTaskAsync();
        _processMonitorTask = StartProcessMonitoringTaskAsync();

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
        await DrainPendingCommandsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var fallbackTimeoutSec = _workerOptions.FallbackPollingIntervalSeconds;
            var notificationReceived = await WaitForNotificationAsync(conn, TimeSpan.FromSeconds(fallbackTimeoutSec), stoppingToken);

            if (notificationReceived)
            {
                logger.LogDebug("NOTIFY {Channel}", ListenChannel);
            }
            else
            {
                logger.LogInformation("Heartbeat: poll pending ({TimeoutSec}s)", fallbackTimeoutSec);
            }

            await DrainPendingCommandsAsync(stoppingToken);
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

    private Task StartCleanupTaskAsync()
    {
        return StartPeriodicBackgroundTaskAsync(
            intervalSeconds: _workerOptions.CleanupIntervalSeconds,
            disabledMessage: interval => $"Cleanup disabled: interval={interval}s",
            cycleName: "lease cleanup",
            cycle: commandDataService.ReleaseExpiredLeasesAsync);
    }

    private Task StartProcessMonitoringTaskAsync()
    {
        var intervalSeconds = RoundUpTo30Seconds(_workerOptions.ProcessMonitorIntervalSeconds);
        return StartPeriodicBackgroundTaskAsync(
            intervalSeconds: intervalSeconds,
            disabledMessage: interval => $"Process monitor disabled: interval={interval}s",
            cycleName: "process monitor",
            cycle: () =>
            {
                CheckProcessesHealth();
                return Task.CompletedTask;
            });
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

                if (health.Status == TelegramBot.BimLib.Models.RevitProcessStatus.NotResponding)
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

        LogActiveProcessesOnShutdown();

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
        await WaitForBackgroundTaskCompletionAsync(_cleanupTask, "Cleanup task", shutdownBudgetCts.Token, Math.Min(TaskWaitTimeoutSeconds, Remaining()));
        await WaitForBackgroundTaskCompletionAsync(_processMonitorTask, "Process monitoring task", shutdownBudgetCts.Token, Math.Min(TaskWaitTimeoutSeconds, Remaining()));
#pragma warning restore VSTHRD003
        await WaitForRunningTasksCompletionAsync(shutdownBudgetCts.Token, Math.Min(TaskWaitTimeoutSeconds, Remaining()));

        _shutdownCts?.Dispose();
        _drainGate.Dispose();

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

    private void LogActiveProcessesOnShutdown()
    {
        var activeCount = processRunner.ActiveProcesses.Count(item => !item.Value.HasExited);
        logger.LogInformation("Shutdown: active={Count}", activeCount);
    }

    private async Task WaitForBackgroundTaskCompletionAsync(Task? task, string taskName, CancellationToken shutdownToken, int timeoutSeconds)
    {
        if (task == null)
        {
            return;
        }

        if (shutdownToken.IsCancellationRequested)
        {
            logger.LogWarning("{TaskName} skip: budget exhausted", taskName);
            return;
        }

        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), shutdownToken);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("{TaskName} timeout ({Timeout}s)", taskName, timeoutSeconds);
        }
        catch (OperationCanceledException)
        {
            // штатное завершение фоновой задачи при остановке Worker
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{TaskName} shutdown fail", taskName);
        }
    }

    private async Task WaitForRunningTasksCompletionAsync(CancellationToken shutdownToken, int timeoutSeconds)
    {
        Task[] runningTasks;
        lock (_runningTasksLock)
        {
            runningTasks = _runningTasks.ToArray();
        }

        if (runningTasks.Length == 0)
        {
            return;
        }

        if (shutdownToken.IsCancellationRequested)
        {
            logger.LogWarning("Task wait skip: budget exhausted");
            return;
        }

        logger.LogInformation("Wait {Timeout}s for {Count} task(s)", timeoutSeconds, runningTasks.Length);

        try
        {
            await Task.WhenAll(runningTasks).WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), shutdownToken);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("{Count} task(s) timeout ({Timeout}s)",
                runningTasks.Count(task => !task.IsCompleted), timeoutSeconds);
        }
        catch (OperationCanceledException)
        {
            // штатное завершение command task'ов при остановке Worker
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Task shutdown fail");
        }
    }

    /// <summary>
    /// Drain-цикл: claim'ит команды, запускает их без ожидания и сразу пытается claim'ить ещё,
    /// пока очередь не опустеет. Это устраняет head-of-line blocking, при котором одна
    /// долгая команда (3h timeout Revit) блокировала запуск остальных 4 из батча.
    /// </summary>
    private async Task DrainPendingCommandsAsync(CancellationToken ct)
    {
        if (!await _drainGate.WaitAsync(0, ct))
        {
            return;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                PruneCompletedTasks();

                var availableSlots = _maxConcurrentCommands - GetRunningTaskCount();
                if (availableSlots <= 0)
                {
                    break;
                }

                // Lease = ProcessTimeoutMinutes + 5 мин буфер для crash recovery
                var leaseTimeoutMinutes = _workerOptions.ProcessTimeoutMinutes + 5;
                var claimLimit = Math.Min(DefaultBatchSize, availableSlots);
                var claimed = await commandDataService.ClaimPendingCommandsAsync(claimLimit, leaseTimeoutMinutes);

                if (claimed.Count == 0)
                {
                    break;
                }

                logger.LogInformation("Claimed: {Count}", claimed.Count);

                foreach (var cmd in claimed)
                {
                    var task = ProcessCommandAsync(cmd, ct);

                    lock (_runningTasksLock)
                    {
                        _ = _runningTasks.Add(task);
                    }

                    // Удаляем завершённые задачи из трекинга.
                    // Используем CancellationToken.None, чтобы ContinueWith выполнялся
                    // всегда — даже при отмене ct (shutdown).
                    _ = task.ContinueWith(completed =>
                    {
                        lock (_runningTasksLock)
                        {
                            _=_runningTasks.Remove(completed);
                        }

                        if (!ct.IsCancellationRequested)
                        {
                            _ = DrainPendingCommandsAsync(ct);
                        }
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Drain error");
        }
        finally
        {
            _ = _drainGate.Release();
        }
    }

    private async Task ProcessCommandAsync(PendingCommand cmd, CancellationToken ct)
    {
        try
        {
            await processRunner.RunAsync(cmd, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Exec error: id={CommandId}, corr={CorrelationId}",
                cmd.CommandId, cmd.CorrelationId);
        }
    }

    private int GetRunningTaskCount()
    {
        lock (_runningTasksLock)
        {
            return _runningTasks.Count;
        }
    }

    private void PruneCompletedTasks()
    {
        lock (_runningTasksLock)
        {
            _=_runningTasks.RemoveWhere(task => task.IsCompleted);
        }
    }
}
