using Microsoft.Extensions.Options;
using TelegramBot.Core.Config;
using TelegramBot.Core.Models;
using TelegramBot.Data;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Забирает доступные команды по запросу единственного polling-цикла Worker.
/// SQL обеспечивает одну команду на файл; tracked tasks ограничивают параллельность.
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

    public int RunningTaskCount
    {
        get { lock (_runningTasksLock) return _runningTasks.Count; }
    }

    public Task[] SnapshotRunningTasks()
    {
        lock (_runningTasksLock) return [.. _runningTasks];
    }

    /// <summary>Забрать доступные команды; вызывается только циклом очереди.</summary>
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
                }
            }
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        catch (Exception ex)
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
        _drainGate.Dispose();
    }
}
