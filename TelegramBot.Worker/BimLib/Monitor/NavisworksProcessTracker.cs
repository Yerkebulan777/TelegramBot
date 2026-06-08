using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TelegramBot.BimLib.Models;

namespace TelegramBot.BimLib.Monitor;

/// <summary>
/// Мониторинг здоровья процессов Navisworks (Roamer.exe, FileConvert.exe).
/// </summary>
internal sealed class NavisworksProcessTracker(
    ILogger<NavisworksProcessTracker> logger)
{
    private static readonly string[] NavisworksProcessNames = ["Roamer", "FileConvert", "NWD"];

    /// <summary>Возвращает список всех активных (не завершённых) процессов Navisworks (Roamer, FileConvert, NWD).</summary>
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

    public RevitProcessHealth CheckHealth(Process process)
        => ProcessHealthHelper.CheckHealth(process, logger, "Navisworks");
}
