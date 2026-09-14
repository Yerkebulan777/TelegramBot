using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics;
using TelegramBot.Core.Config;
using TelegramBot.Data;
using TelegramBot.BimLib.Monitor;
using TelegramBot.Worker.Helpers;

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
        _processMonitorTask = StartPeriodicBackgroundTaskAsync(
            intervalSeconds: _workerOptions.ProcessMonitorIntervalSeconds > 0
                ? RoundUpTo30Seconds(_workerOptions.ProcessMonitorIntervalSeconds) : 0,
            disabledMessage: interval => $"Process monitor disabled: interval={interval}s",
            cycleName: "process monitor",
            cycle: () => { CheckProcessesHealth(); return Task.CompletedTask; });

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

        // Чистим «висячие» записи: команда могла завершиться, пока числилась NotResponding, и
        // выйти из ActiveProcesses между тиками монитора — иначе запись остаётся в _unresponsiveSince навсегда.
        var activeIds = processRunner.ActiveProcesses.Select(p => p.Key).ToHashSet();
        foreach (var staleCommandId in _unresponsiveSince.Keys.Where(id => !activeIds.Contains(id)).ToList())
        {
            _ = _unresponsiveSince.TryRemove(staleCommandId, out _);
        }

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
        await WaitForTasksAsync(_processMonitorTask, "Process monitor", shutdownBudgetCts.Token, Math.Min(TaskWaitTimeoutSeconds, Remaining()));
        await WaitForTasksAsync(null, "Running tasks", shutdownBudgetCts.Token, Math.Min(TaskWaitTimeoutSeconds, Remaining()));
#pragma warning restore VSTHRD003

        await processRunner.ReleaseClaimedLeasesOnShutdownAsync();

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
