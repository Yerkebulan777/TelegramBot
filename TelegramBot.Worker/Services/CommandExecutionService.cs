using Microsoft.Extensions.Options;
using Npgsql;
using System.Diagnostics;
using TelegramBot.Core.Config;
using TelegramBot.Core.Models;
using TelegramBot.Data;
using TelegramBot.Worker.BimLib.Monitor;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Background service: orchestrator для событийной обработки очереди команд через PostgreSQL LISTEN/NOTIFY.
/// Делегирует выполнение специализированным компонентам: PartitionPoolManager, ProcessRunner, SessionCompletionTracker.
/// </summary>
public sealed class CommandExecutionService(
    CommandDataService commandDataService,
    PartitionPoolManager partitionPoolManager,
    ProcessRunner processRunner,
    SessionCompletionTracker sessionCompletionTracker,
    IOptions<WorkerOptions> workerOptions,
    IConfiguration configuration,
    ILogger<CommandExecutionService> logger,
    DialogDismisser dialogDismisser) : BackgroundService
{
    private const string ListenChannel = "new_tasks";
    private const int DefaultBatchSize = 5;
    private const int ShutdownBudgetSeconds = 30;

    // Трекинг выполняемых задач для корректного ожидания при shutdown
    private readonly HashSet<Task> _runningTasks = [];
    private readonly object _runningTasksLock = new();
    private readonly SemaphoreSlim _drainGate = new(1, 1);

    private readonly string _connectionString = configuration.GetConnectionString("Postgres")
        ?? DataAccessBase.DefaultConnectionString;
    private readonly WorkerOptions _workerOptions = workerOptions.Value;

    private CancellationTokenSource? _shutdownCts;
    private Task? _cleanupTask;
    private Task? _healthTask;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        partitionPoolManager.Initialize(_workerOptions.Partitions);

        logger.LogInformation("Worker starting: partitions={PartitionCount}, pools={Pools}",
            partitionPoolManager.PoolCount, partitionPoolManager.GetPoolInfo());

        _shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        _cleanupTask = StartCleanupTaskAsync();
        _healthTask = StartHealthMonitoringTaskAsync();

        try
        {
            await PostgresReconnectLoop.RunAsync(
                "Worker",
                RunListenerLoopAsync,
                logger,
                stoppingToken: stoppingToken);
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
                logger.LogDebug("Fallback polling triggered after {TimeoutSec}s", fallbackTimeoutSec);
            }

            await DrainPendingCommandsAsync(stoppingToken);
        }
    }

    private async Task<bool> WaitForNotificationAsync(NpgsqlConnection conn, TimeSpan timeout, CancellationToken ct)
    {
        var notificationReceived = false;
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

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
            await conn.WaitAsync(timeoutCts.Token);
            return notificationReceived;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
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
            timeoutCts.Dispose();
        }
    }

    private Task StartCleanupTaskAsync()
    {
        return Task.Run(async () =>
        {
            if (_workerOptions.CleanupIntervalSeconds <= 0)
            {
                logger.LogWarning("CleanupIntervalSeconds = {Interval}, lease cleanup disabled",
                    _workerOptions.CleanupIntervalSeconds);
                return;
            }

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_workerOptions.CleanupIntervalSeconds));

            try
            {
                while (await timer.WaitForNextTickAsync(_shutdownCts!.Token))
                {
                    try
                    {
                        await commandDataService.ReleaseExpiredLeasesAsync();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error in lease cleanup cycle");
                    }
                }
            }
            catch (OperationCanceledException) when (_shutdownCts?.IsCancellationRequested == true)
            {
                // штатное завершение
            }
        });
    }

    private Task StartHealthMonitoringTaskAsync()
    {
        return Task.Run(async () =>
        {
            if (_workerOptions.HealthCheckIntervalSeconds <= 0)
            {
                logger.LogWarning("HealthCheckIntervalSeconds = {Interval}, process health monitoring disabled",
                    _workerOptions.HealthCheckIntervalSeconds);
                return;
            }

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_workerOptions.HealthCheckIntervalSeconds));

            try
            {
                while (await timer.WaitForNextTickAsync(_shutdownCts!.Token))
                {
                    try
                    {
                        CheckProcessesHealth();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error in process health check cycle");
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

        // Ждем завершения фоновых задач в оставшемся общем бюджете.
#pragma warning disable VSTHRD003
        await WaitForBackgroundTaskCompletionAsync(_cleanupTask, "Cleanup task", shutdownBudgetCts.Token);
        await WaitForBackgroundTaskCompletionAsync(_healthTask, "Health monitoring task", shutdownBudgetCts.Token);
        await WaitForRunningTasksCompletionAsync(shutdownBudgetCts.Token);
#pragma warning restore VSTHRD003

        _shutdownCts?.Dispose();
        _drainGate.Dispose();
        partitionPoolManager.Dispose();

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

                process.Kill(entireProcessTree: true);

                using var perProcessCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
                perProcessCts.CancelAfter(TimeSpan.FromSeconds(10));

                try
                {
                    await process.WaitForExitAsync(perProcessCts.Token);
                }
                catch (OperationCanceledException) when (perProcessCts.IsCancellationRequested)
                {
                }

                if (!process.HasExited)
                {
                    logger.LogWarning(
                        "Process did not exit after kill: commandId={Id}, pid={Pid}",
                        commandId, process.Id);
                }
                else
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

    private async Task WaitForBackgroundTaskCompletionAsync(Task? task, string taskName, CancellationToken shutdownToken)
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

        var timeout = Task.Delay(TimeSpan.FromSeconds(15), shutdownToken);
#pragma warning disable VSTHRD003
        if (await Task.WhenAny(task, timeout) != task)
#pragma warning restore VSTHRD003
        {
            logger.LogWarning("{TaskName} did not complete within 15s timeout", taskName);
        }
        else
        {
            try
            {
#pragma warning disable VSTHRD003
                await task;
#pragma warning restore VSTHRD003
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
    }

    private async Task WaitForRunningTasksCompletionAsync(CancellationToken shutdownToken)
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

        logger.LogInformation("Waiting up to 15s for {Count} command task(s) to stop", runningTasks.Length);

        var allTasks = Task.WhenAll(runningTasks);
        var timeout = Task.Delay(TimeSpan.FromSeconds(15), shutdownToken);
        if (await Task.WhenAny(allTasks, timeout) != allTasks)
        {
            logger.LogWarning("{Count} command task(s) did not complete within 15s timeout",
                runningTasks.Count(task => !task.IsCompleted));
        }
        else
        {
            try
            {
                await allTasks;
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

                var availableSlots = partitionPoolManager.TotalCapacity - GetRunningTaskCount();
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

                sessionCompletionTracker.TrackClaimedCommands(claimed);

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
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
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
        await partitionPoolManager.WaitForSlotAsync(cmd.Priority, ct);
        var slotAcquired = true;
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
            if (slotAcquired)
            {
                partitionPoolManager.ReleaseSlot(cmd.Priority);
            }
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
