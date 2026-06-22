using System.Diagnostics;
using TelegramBot.Worker.BimLib.Models;

namespace TelegramBot.Worker.BimLib.Monitor;

/// <summary>
/// Мониторинг здоровья процессов Navisworks (Roamer.exe, FileConvert.exe).
/// </summary>
internal sealed class NavisworksProcessTracker(
    ILogger<NavisworksProcessTracker> logger)
{
    private static readonly string[] _navisworksProcessNames = ["Roamer", "FileConvert", "NWD"];

    /// <summary>Возвращает список всех активных (не завершённых) процессов Navisworks (Roamer, FileConvert, NWD).
    /// Возвращённые <see cref="Process"/> экземпляры должны быть Disposed вызывающей стороной.
    /// Каждый Process из Process.GetProcessesByName требует явного Dispose для освобождения native handle.</summary>
    public IReadOnlyList<Process> GetAllProcesses()
    {
        var result = new List<Process>();

        foreach (var processName in _navisworksProcessNames)
        {
            try
            {
                var snapshot = Process.GetProcessesByName(processName);
                foreach (var p in snapshot)
                {
                    try
                    {
                        if (!p.HasExited) result.Add(p);
                        else p.Dispose();
                    }
                    catch (InvalidOperationException)
                    {
                        p.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to enumerate processes by name '{Name}'", processName);
            }
        }

        return result;
    }

    public RevitProcessHealth CheckHealth(Process process)
    {
        return ProcessHealthHelper.CheckHealth(process, logger, "Navisworks");
    }
}
