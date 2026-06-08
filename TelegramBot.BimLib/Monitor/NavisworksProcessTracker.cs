using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TelegramBot.BimLib.Interfaces;
using TelegramBot.BimLib.Models;

namespace TelegramBot.BimLib.Monitor;

/// <summary>
/// Мониторинг здоровья процессов Navisworks (Roamer.exe, FileConvert.exe).
/// </summary>
internal sealed class NavisworksProcessTracker(
    ILogger<NavisworksProcessTracker> logger) : INavisworksProcessTracker
{
    private static readonly string[] NavisworksProcessNames = ["Roamer", "FileConvert", "NWD"];

    /// <inheritdoc/>
    public IReadOnlyList<Process> GetAllProcesses()
    {
        var result = new List<Process>();

        foreach (var processName in NavisworksProcessNames)
        {
            try
            {
                result.AddRange(Process.GetProcessesByName(processName)
                    .Where(p =>
                    {
                        try { return !p.HasExited; }
                        catch { return false; }
                    }));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to enumerate processes by name '{Name}'", processName);
            }
        }

        return result;
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

            return new RevitProcessHealth(process.Id, status, memoryMb, duration, responding);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to check health for Navisworks process {ProcessId}", process.Id);
            return new RevitProcessHealth(process.Id, RevitProcessStatus.Error, 0, TimeSpan.Zero, false);
        }
    }
}
