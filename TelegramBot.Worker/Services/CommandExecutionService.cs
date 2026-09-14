using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics;
using TelegramBot.Core.Config;
using TelegramBot.Data;
using TelegramBot.BimLib.Monitor;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Единственный цикл очереди PostgreSQL; выполнение делегирует <see cref="CommandOrchestrator"/>.
/// Очистка lease предшествует claim; мониторинг процессов работает независимо.
/// </summary>
public sealed class CommandExecutionService(
    CommandDataService commandDataService,
    CommandOrchestrator orchestrator,
    ProcessRunner processRunner,
    SchemaReadyGate schemaReadyGate,
    IOptions<WorkerOptions> workerOptions,
    ILogger<CommandExecutionService> logger,
    DialogDismisser dialogDismisser) : BackgroundService
{
    private const int ShutdownBudgetSeconds = 30;
    private const int TaskWaitTimeoutSeconds = 15;

    private readonly ConcurrentDictionary<int, DateTime> _unresponsiveSince = new();

    private readonly WorkerOptions _workerOptions = workerOptions.Value;

    private CancellationTokenSource? _shutdownCts;
    private Task? _processMonitorTask;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollSeconds = _workerOptions.FallbackPollingIntervalSeconds > 0
            ? _workerOptions.FallbackPollingIntervalSeconds : 1;
        logger.LogInformation("Worker start: maxC={MaxConcurrentCommands}, poll={PollSeconds}s",
            _workerOptions.MaxConcurrentCommands, pollSeconds);
        await schemaReadyGate.WaitAsync(stoppingToken);
        _shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _processMonitorTask = StartPeriodicBackgroundTaskAsync();

        var nextCleanup = DateTime.MinValue;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (DateTime.UtcNow >= nextCleanup)
                    {
                        await commandDataService.ReleaseExpiredLeasesAsync(_workerOptions.MaxRetries);
                        nextCleanup = _workerOptions.CleanupIntervalSeconds > 0
                            ? DateTime.UtcNow.AddSeconds(_workerOptions.CleanupIntervalSeconds)
                            : DateTime.MaxValue;
                    }

                    await orchestrator.TriggerDrainAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Queue poll failed: retry in {PollSeconds}s", pollSeconds);
                }

                // A delay after each attempt also bounds retries when PostgreSQL is unavailable.
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        finally
        {
            await PerformGracefulShutdownAsync();
        }

        logger.LogInformation("Worker stop");
    }

    /// <summary>
    /// Мониторинг процессов на <see cref="PeriodicTimer"/> с обработкой ошибок итерации и shutdown.
    /// </summary>
    private Task StartPeriodicBackgroundTaskAsync()
    {
        return Task.Run(async () =>
        {
            var intervalSeconds = _workerOptions.ProcessMonitorIntervalSeconds > 0
                ? RoundUpTo30Seconds(_workerOptions.ProcessMonitorIntervalSeconds) : 0;
            if (intervalSeconds <= 0)
            {
                logger.LogWarning("Process monitor disabled: interval={IntervalSeconds}s", intervalSeconds);
                return;
            }

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

            try
            {
                while (await timer.WaitForNextTickAsync(_shutdownCts!.Token))
                {
                    try
                    {
                        await CheckProcessesHealthAsync();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error in {Cycle}", "process monitor");
                    }
                }
            }
            catch (OperationCanceledException) when (_shutdownCts?.IsCancellationRequested == true)
            {
                // штатное завершение
            }
        });
    }

    private async Task CheckProcessesHealthAsync()
    {
        var thresholdSeconds = Math.Max(
            RoundUpTo30Seconds(_workerOptions.UnresponsiveThresholdSeconds),
            RoundUpTo30Seconds(_workerOptions.ProcessMonitorIntervalSeconds));

        // Чистим «висячие» записи: команда могла завершиться, пока числилась NotResponding, и
        // выйти из ActiveProcesses между тиками монитора — иначе запись остаётся в _unresponsiveSince навсегда.
        var activeIds = processRunner.ActiveProcesses.Select(p => p.Key).ToHashSet();
        foreach (var staleCommandId in _unresponsiveSince.Keys.Where(id => !activeIds.Contains(id)).ToList())
        {
            _ = _unresponsiveSince.TryRemove(staleCommandId, out _);
        }

        dialogDismisser.PruneInactiveCommands(activeIds);

        foreach (var (commandId, process) in processRunner.ActiveProcesses)
        {
            if (process.HasExited)
            {
                _ = _unresponsiveSince.TryRemove(commandId, out _);
                continue;
            }

            try
            {
                var responding = true;
                var memoryMb = 0L;
                var duration = TimeSpan.Zero;
                try
                {
                    responding = process.Responding;
                    memoryMb = process.WorkingSet64 / (1024 * 1024);
                    duration = DateTime.UtcNow - process.StartTime.ToUniversalTime();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Health check fail: Command#{CommandId} pid={ProcessId}",
                        commandId, process.Id);
                }

                if (!responding)
                {
                    var since = _unresponsiveSince.GetOrAdd(commandId, _ => DateTime.UtcNow);
                    var stuckFor = DateTime.UtcNow - since;
                    if (stuckFor.TotalSeconds >= thresholdSeconds)
                    {
                        logger.LogWarning(
                            "Not responding: id={Id}, pid={Pid}, stuck={StuckFor}, mem={MemoryMb}MB, dur={Duration}",
                            commandId, process.Id, stuckFor, memoryMb, duration);
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
                    if (dialogDismisser.NeedsProcessKillAfterDismiss(commandId, (uint)process.Id))
                    {
                        await processRunner.KillTrackedProcessAsync(
                            commandId, _shutdownCts?.Token ?? CancellationToken.None);
                    }
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

        try
        {
            await processRunner.KillAllTrackedAsync(shutdownBudgetCts.Token);
        }
        catch (OperationCanceledException) when (shutdownBudgetCts.IsCancellationRequested)
        {
            logger.LogWarning("Shutdown kill >{BudgetSeconds}s", ShutdownBudgetSeconds);
        }

        int Remaining() => Math.Max(1, ShutdownBudgetSeconds - (int)(DateTime.UtcNow - shutdownStartedAt).TotalSeconds);
#pragma warning disable VSTHRD003
        await WaitForTasksAsync(_processMonitorTask, "Process monitor", shutdownBudgetCts.Token, Math.Min(TaskWaitTimeoutSeconds, Remaining()));
        await WaitForTasksAsync(null, "Running tasks", shutdownBudgetCts.Token, Math.Min(TaskWaitTimeoutSeconds, Remaining()));
#pragma warning restore VSTHRD003

        await processRunner.ReleaseClaimedLeasesOnShutdownAsync();

        _shutdownCts?.Dispose();

        logger.LogInformation("Shutdown done");
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
