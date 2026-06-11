using System.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;
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
    SessionDataService sessionDataService,
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

    // Трекинг выполняемых задач для корректного ожидания при shutdown
    private readonly HashSet<Task> _runningTasks = [];
    private readonly object _runningTasksLock = new();

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
        await commandDataService.ReleaseExpiredLeasesAsync();

        await DrainPendingCommandsAsync(stoppingToken);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(stoppingToken);

        await using var cmd = new NpgsqlCommand($"LISTEN {ListenChannel};", conn);
        _ = await cmd.ExecuteNonQueryAsync(stoppingToken);

        logger.LogInformation("Listening for notifications on channel '{Channel}'", ListenChannel);

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
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_workerOptions.CleanupIntervalSeconds));

            while (await timer.WaitForNextTickAsync(_shutdownCts!.Token))
            {
                try
                {
                    await commandDataService.ReleaseExpiredLeasesAsync();
                    await CleanupInactiveSessionsAsync();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in lease cleanup cycle");
                }
            }
        });
    }

    private Task StartHealthMonitoringTaskAsync()
    {
        return Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_workerOptions.HealthCheckIntervalSeconds));

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

        // Шаг 1: Останавливаем прием новых задач
        await LogActiveProcessesOnShutdownAsync();

        // Шаг 2: Принудительно завершаем все активные процессы
        // Даём процессам до 10 секунд на завершение после Kill
        var processesToKill = processRunner.ActiveProcesses.ToList();
        var killTasks = processesToKill.Select(kvp => KillProcessAsync(kvp.Key, kvp.Value)).ToList();
        
        if (killTasks.Count > 0)
        {
            await Task.WhenAll(killTasks);
        }

        // Шаг 3: Отменяем фоновые задачи
        if (_shutdownCts != null) await _shutdownCts.CancelAsync();

        // Шаг 4: Ждем завершения фоновых задач (максимум 15 секунд)
#pragma warning disable VSTHRD003
        await WaitForBackgroundTaskCompletionAsync(_cleanupTask, "Cleanup task");
        await WaitForBackgroundTaskCompletionAsync(_healthTask, "Health monitoring task");
#pragma warning restore VSTHRD003

        // Шаг 5: Освобождаем ресурсы
        _shutdownCts?.Dispose();
        partitionPoolManager.Dispose();
        
        logger.LogInformation("Worker shutdown completed");
    }

    private async Task KillProcessAsync(int commandId, Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                logger.LogInformation("Killing process on shutdown: commandId={Id}, pid={Pid}",
                    commandId, process.Id);

                process.Kill(entireProcessTree: true);

                // Ждём до 10 секунд, чтобы процесс успел закрыть файлы
                var exitTask = process.WaitForExitAsync(CancellationToken.None);
                var timeoutTask = Task.Delay(10_000);
                
                await Task.WhenAny(exitTask, timeoutTask);

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

    private async Task LogActiveProcessesOnShutdownAsync()
    {
        var activeSnapshot = processRunner.ActiveProcesses.ToList();

        if (activeSnapshot.Count == 0)
        {
            logger.LogInformation("Worker shutdown: no active processes to handle");
            return;
        }

        logger.LogInformation("Worker shutdown: waiting up to 30s for {Count} active processes",
            activeSnapshot.Count);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && activeSnapshot.Any(p => !p.Value.HasExited))
        {
            await Task.Delay(500);
        }

        var stillRunning = activeSnapshot.Count(p => !p.Value.HasExited);
        if (stillRunning > 0)
        {
            logger.LogInformation("Worker shutdown: {Count} processes still running after 30s, leaving them",
                stillRunning);
        }

        foreach (var (commandId, process) in activeSnapshot)
        {
            if (process.HasExited) continue;
            logger.LogInformation("Process left running: commandId={Id}, pid={Pid}", commandId, process.Id);
        }
    }

    private async Task WaitForBackgroundTaskCompletionAsync(Task? task, string taskName)
    {
        if (task == null) return;

        var timeout = Task.Delay(TimeSpan.FromSeconds(15), CancellationToken.None);
#pragma warning disable VSTHRD003
        if (await Task.WhenAny(task, timeout) != task)
#pragma warning restore VSTHRD003
        {
            logger.LogWarning("{TaskName} did not complete within 15s timeout", taskName);
        }
    }

    private async Task CleanupInactiveSessionsAsync()
    {
        if (_workerOptions.CompletedSessionRetentionDays <= 0)
        {
            return;
        }

        var cutoffUtc = DateTime.UtcNow.AddDays(-_workerOptions.CompletedSessionRetentionDays);
        var deletedCount = await sessionDataService.SoftDeleteInactiveSessionsOlderThanAsync(cutoffUtc);
        if (deletedCount > 0)
        {
            logger.LogInformation(
                "Inactive session cleanup completed: deleted={Count}, retentionDays={RetentionDays}",
                deletedCount, _workerOptions.CompletedSessionRetentionDays);
        }
    }

    /// <summary>
    /// Drain-цикл: claim'ит команды, запускает их без ожидания и сразу пытается claim'ить ещё,
    /// пока очередь не опустеет. Это устраняет head-of-line blocking, при котором одна
    /// долгая команда (3h timeout Revit) блокировала запуск остальных 4 из батча.
    /// </summary>
    private async Task DrainPendingCommandsAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Lease = ProcessTimeoutMinutes + 5 мин буфер для crash recovery
                var leaseTimeoutMinutes = _workerOptions.ProcessTimeoutMinutes + 5;
                var claimed = await commandDataService.ClaimPendingCommandsAsync(DefaultBatchSize, leaseTimeoutMinutes);

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
                            _runningTasks.Remove(completed);
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
    }

    private async Task ProcessWithPoolAsync(PendingCommand cmd, CancellationToken ct)
    {
        await partitionPoolManager.WaitForSlotAsync(cmd.Priority, ct);

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
            partitionPoolManager.ReleaseSlot(cmd.Priority);
        }
    }
}
