using Microsoft.Extensions.Options;
using Npgsql;
using System.Diagnostics;
using TelegramBot.Core.Config;
using TelegramBot.Core.Models;
using TelegramBot.Data;
using TelegramBot.Worker.BimLib.Monitor;
using TelegramBot.Worker.Helpers;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Background service: orchestrator для событийной обработки очереди команд через PostgreSQL LISTEN/NOTIFY.
/// Делегирует выполнение специализированным компонентам: ProcessRunner и SessionCompletionTracker.
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
    private readonly object _runningTasksLock = new();
    private readonly SemaphoreSlim _drainGate = new(1, 1);
    private readonly int _maxConcurrentCommands = Math.Max(1, workerOptions.Value.Partitions.Sum(p => p.Value));
    private SemaphoreSlim? _commandSlots;

    private readonly string _connectionString = configuration.GetConnectionString("Postgres")
        ?? DataAccessBase.DefaultConnectionString;
    private readonly WorkerOptions _workerOptions = workerOptions.Value;

    private CancellationTokenSource? _shutdownCts;
    private Task? _cleanupTask;
    private Task? _processMonitorTask;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _commandSlots = new SemaphoreSlim(_maxConcurrentCommands, _maxConcurrentCommands);

        logger.LogInformation("Worker starting: maxConcurrentCommands={MaxConcurrentCommands}", _maxConcurrentCommands);

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
                    logger.LogError(ex, "Worker listener lost: retryMs={Delay}", reconnectDelayMs);
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

        logger.LogInformation("Worker stopped");
    }

    private async Task RunListenerLoopAsync(CancellationToken stoppingToken)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(stoppingToken);

        await using var cmd = new NpgsqlCommand($"LISTEN {ListenChannel};", conn);
        _ = await cmd.ExecuteNonQueryAsync(stoppingToken);

        logger.LogInformation("Listening for notifications on channel '{Channel}'", ListenChannel);

        await commandDataService.ReleaseExpiredLeasesAsync();
        await DrainPendingCommandsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var fallbackTimeoutSec = _workerOptions.FallbackPollingIntervalSeconds;
            var notificationReceived = await WaitForNotificationAsync(conn, TimeSpan.FromSeconds(fallbackTimeoutSec), stoppingToken);

            if (notificationReceived)
            {
                logger.LogDebug("Notification received on channel '{Channel}'", ListenChannel);
            }
            else
            {
                logger.LogInformation("Worker heartbeat: checking pending commands after {TimeoutSec}s without PostgreSQL notification", fallbackTimeoutSec);
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
            await conn.WaitAsync(timeout, ct);
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
            disabledMessage: interval => $"CleanupIntervalSeconds = {interval}, lease cleanup disabled",
            cycleName: "lease cleanup cycle",
            cycle: () => commandDataService.ReleaseExpiredLeasesAsync());
    }

    private Task StartProcessMonitoringTaskAsync()
    {
        return StartPeriodicBackgroundTaskAsync(
            intervalSeconds: _workerOptions.ProcessMonitorIntervalSeconds,
            disabledMessage: interval => $"ProcessMonitorIntervalSeconds = {interval}, process monitoring disabled",
            cycleName: "process monitor cycle",
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
        foreach (var (commandId, process) in processRunner.ActiveProcesses)
        {
            if (process.HasExited)
            {
                continue;
            }

            try
            {
                var health = ProcessHealthHelper.CheckHealth(process, logger, $"Command#{commandId}");

                if (health.Status == BimLib.Models.RevitProcessStatus.NotResponding)
                {
                    logger.LogWarning(
                        "Process not responding: commandId={Id}, pid={Pid}, memoryMb={MemoryMb}, duration={Duration}",
                        commandId, process.Id, health.MemoryMb, health.Duration);
                }
                else if (health.Status == BimLib.Models.RevitProcessStatus.Healthy)
                {
                    logger.LogDebug(
                        "Process healthy: commandId={Id}, pid={Pid}, memoryMb={MemoryMb}, duration={Duration}",
                        commandId, process.Id, health.MemoryMb, health.Duration);
                }

                try
                {
                    _ = dialogDismisser.DismissDialogsForProcess((uint)process.Id);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to dismiss dialogs for command {CommandId}", commandId);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Health check failed for command {CommandId}", commandId);
            }
        }
    }

    private async Task PerformGracefulShutdownAsync()
    {
        logger.LogInformation("Worker stopping: initiating graceful shutdown...");

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
                logger.LogWarning("Worker shutdown kill phase exceeded {BudgetSeconds}s budget", ShutdownBudgetSeconds);
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
        _commandSlots?.Dispose();

        logger.LogInformation("Worker shutdown completed");
    }

    private async Task KillProcessAsync(int commandId, Process process, CancellationToken shutdownToken)
    {
        try
        {
            if (!process.HasExited)
            {
                logger.LogInformation("Killing process on shutdown: commandId={Id}, pid={Pid}",
                    commandId, process.Id);

                var exited = await ProcessKillHelper.KillAsync(
                    process, TimeSpan.FromSeconds(10), logger, commandId, shutdownToken);

                if (exited)
                {
                    logger.LogInformation("Process killed successfully: commandId={Id}, pid={Pid}",
                        commandId, process.Id);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error killing process on shutdown: commandId={Id}, pid={Pid}",
                commandId, process.Id);
        }
        finally
        {
            process.Dispose();
        }
    }

    private void LogActiveProcessesOnShutdown()
    {
        var activeSnapshot = processRunner.ActiveProcesses.ToList();

        if (activeSnapshot.Count == 0)
        {
            logger.LogInformation("Worker shutdown: no active processes to handle");
            return;
        }

        logger.LogInformation("Worker shutdown: killing {Count} active process(es)",
            activeSnapshot.Count);

        foreach (var (commandId, process) in activeSnapshot)
        {
            if (process.HasExited)
            {
                continue;
            }

            logger.LogInformation("Active process on shutdown: commandId={Id}, pid={Pid}", commandId, process.Id);
        }
    }

    private async Task WaitForBackgroundTaskCompletionAsync(Task? task, string taskName, CancellationToken shutdownToken, int timeoutSeconds)
    {
        if (task == null)
        {
            return;
        }

        if (shutdownToken.IsCancellationRequested)
        {
            logger.LogWarning("{TaskName} skipped: shutdown budget exhausted", taskName);
            return;
        }

        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), shutdownToken);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("{TaskName} did not complete within {Timeout}s timeout", taskName, timeoutSeconds);
        }
        catch (OperationCanceledException)
        {
            // штатное завершение фоновой задачи при остановке Worker
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{TaskName} failed during shutdown", taskName);
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
            logger.LogWarning("Command task wait skipped: shutdown budget exhausted");
            return;
        }

        logger.LogInformation("Waiting up to {Timeout}s for {Count} command task(s) to stop", timeoutSeconds, runningTasks.Length);

        try
        {
            await Task.WhenAll(runningTasks).WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), shutdownToken);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("{Count} command task(s) did not complete within {Timeout}s timeout",
                runningTasks.Count(task => !task.IsCompleted), timeoutSeconds);
        }
        catch (OperationCanceledException)
        {
            // штатное завершение command task'ов при остановке Worker
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "One or more command tasks failed during shutdown");
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

                logger.LogInformation(
                    "Worker batch claimed: count={Count}, correlationIds={CorrelationIds}",
                    claimed.Count,
                    string.Join(", ", claimed.Select(c => c.CorrelationId).Distinct()));

                foreach (var cmd in claimed)
                {
                    var task = ProcessWithPoolAsync(cmd, ct);

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
            logger.LogError(ex, "Error draining pending commands");
        }
        finally
        {
            _ = _drainGate.Release();
        }
    }

    private async Task ProcessWithPoolAsync(PendingCommand cmd, CancellationToken ct)
    {
        await _commandSlots!.WaitAsync(ct);
        try
        {
            await processRunner.RunAsync(cmd, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error executing command {CommandId}, correlationId={CorrelationId}",
                cmd.CommandId, cmd.CorrelationId);
        }
        finally
        {
            _ = _commandSlots.Release();
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
