using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TelegramBot.BimLib.Interfaces;
using TelegramBot.BimLib.Models;

namespace TelegramBot.BimLib.Monitor;

/// <summary>
/// Мониторинг здоровья процессов Revit: проверка отклика, памяти, 
/// автоматическое закрытие диалогов, трекинг активных процессов.
/// </summary>
internal sealed class RevitProcessTracker(
    DialogDismisser dialogDismisser,
    ILogger<RevitProcessTracker> logger) : IRevitProcessTracker
{
    /// <inheritdoc/>
    public IReadOnlyList<Process> GetAllRevitProcesses()
    {
        return Process.GetProcessesByName("Revit")
            .Where(p =>
            {
                try { return !p.HasExited; }
                catch { return false; }
            })
            .ToList();
    }

    /// <inheritdoc/>
    public RevitProcessHealth CheckHealth(Process process)
    {
        try
        {
            var responding = process.Responding;
            var memoryMb = process.WorkingSet64 / (1024 * 1024);
            var startTime = process.StartTime;
            var duration = DateTime.UtcNow - startTime.ToUniversalTime();

            var status = responding
                ? RevitProcessStatus.Healthy
                : RevitProcessStatus.NotResponding;

            return new RevitProcessHealth(
                process.Id,
                status,
                memoryMb,
                duration,
                responding);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to check health for process {ProcessId}", process.Id);
            return new RevitProcessHealth(process.Id, RevitProcessStatus.Error, 0, TimeSpan.Zero, false);
        }
    }

    /// <inheritdoc/>
    public int DismissDialogs(int processId)
    {
        var dismissed = dialogDismisser.DismissDialogsForProcess((uint)processId);
        if (dismissed)
            logger.LogInformation("Dialogs dismissed for process {ProcessId}", processId);
        return dismissed ? 1 : 0;
    }
}
