using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TelegramBot.BimLib.Models;

namespace TelegramBot.BimLib.Monitor;

/// <summary>
/// Мониторинг здоровья процессов Revit: проверка отклика, памяти, 
/// автоматическое закрытие диалогов, трекинг активных процессов.
/// </summary>
internal sealed class RevitProcessTracker(
    DialogDismisser dialogDismisser,
    ILogger<RevitProcessTracker> logger)
{
    /// <summary>Возвращает список всех активных (не завершённых) процессов Revit.</summary>
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

    public RevitProcessHealth CheckHealth(Process process)
        => ProcessHealthHelper.CheckHealth(process, logger, "Revit");

    /// <summary>Закрывает модальные диалоги Revit для указанного процесса. Возвращает 1, если диалоги были закрыты.</summary>
    public int DismissDialogs(int processId)
    {
        var dismissed = dialogDismisser.DismissDialogsForProcess((uint)processId);
        if (dismissed)
            logger.LogInformation("Dialogs dismissed for process {ProcessId}", processId);
        return dismissed ? 1 : 0;
    }
}
