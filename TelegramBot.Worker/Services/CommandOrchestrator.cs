using Microsoft.Extensions.Options;
using TelegramBot.Core.Config;
using TelegramBot.Core.Models;
using TelegramBot.Data;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Владеет циклом claim → async-launch. Два независимых триггера drain'а: собственный
/// <see cref="PeriodicTimer"/> (safety-net) и внешний <see cref="TriggerDrainAsync"/> (NOTIFY/initial connect).
/// Single-flight через семафор; launch не блокирует следующий claim (fire-and-forget Task),
/// что устраняет head-of-line blocking от долгих (до 3ч) Revit-задач.
/// </summary>
public sealed class CommandOrchestrator(
    CommandDataService commandDataService,
    ProcessRunner processRunner,
    IOptions<WorkerOptions> workerOptions,
    ILogger<CommandOrchestrator> logger) : IDisposable
{
    private const int DefaultBatchSize = 5;

    private readonly WorkerOptions _options = workerOptions.Value;
    private readonly int _maxConcurrentCommands = Math.Max(1, workerOptions.Value.MaxConcurrentCommands);

    private readonly HashSet<Task> _runningTasks = [];
    private readonly object _runningTasksLock = new();
    private readonly SemaphoreSlim _drainGate = new(1, 1);

    private PeriodicTimer? _timer;

    public int RunningTaskCount
    {
        get { lock (_runningTasksLock) return _runningTasks.Count; }
    }

    public Task[] SnapshotRunningTasks()
    {
        lock (_runningTasksLock) return [.. _runningTasks];
    }

    /// <summary>Запускает periodic timer как safety-net. Вызвать один раз при старте воркера.</summary>
    public void StartTimer(CancellationToken ct)
    {
        var intervalSeconds = Math.Max(1, _options.FallbackPollingIntervalSeconds);
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        _ = RunTimerLoopAsync(ct);
    }

    private async Task RunTimerLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _timer!.WaitForNextTickAsync(ct))
            {
                logger.LogInformation("Heartbeat: poll pending");
                await TriggerDrainAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // штатное завершение
        }
    }

    /// <summary>Внешний триггер (NOTIFY / initial connect) — немедленный drain.</summary>
    public async Task TriggerDrainAsync(CancellationToken ct)
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

                var availableSlots = _maxConcurrentCommands - RunningTaskCount;
                if (availableSlots <= 0)
                {
                    break;
                }

                // Lease = ProcessTimeoutMinutes + 5 мин буфер для crash recovery
                var leaseTimeoutMinutes = _options.ProcessTimeoutMinutes + 5;
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

                    // Используем CancellationToken.None, чтобы ContinueWith выполнялся
                    // всегда — даже при отмене ct (shutdown).
                    _ = task.ContinueWith(completed =>
                    {
                        lock (_runningTasksLock)
                        {
                            _ = _runningTasks.Remove(completed);
                        }

                        if (!ct.IsCancellationRequested)
                        {
                            _ = TriggerDrainAsync(ct);
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

    private void PruneCompletedTasks()
    {
        lock (_runningTasksLock)
        {
            _ = _runningTasks.RemoveWhere(task => task.IsCompleted);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _drainGate.Dispose();
    }
}
