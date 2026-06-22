using System.Diagnostics;
using TelegramBot.Worker.BimLib.Models;

namespace TelegramBot.Worker.BimLib.Monitor;

/// <summary>
/// Статический helper для проверки здоровья процесса (responsiveness, memory, duration).
/// Используется CommandExecutionService.CheckProcessesHealth.
/// </summary>
internal static class ProcessHealthHelper
{
    /// <summary>Проверяет здоровье указанного процесса и возвращает RevitProcessHealth.</summary>
    public static RevitProcessHealth CheckHealth(Process process, ILogger logger, string processDisplayName)
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
            logger.LogWarning(ex, "Failed to check health for {ProcessName} process {ProcessId}",
                processDisplayName, process.Id);
            return new RevitProcessHealth(process.Id, RevitProcessStatus.Error, 0, TimeSpan.Zero, false);
        }
    }
}
