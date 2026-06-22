using System.Diagnostics;
using TelegramBot.Worker.BimLib.Models;

namespace TelegramBot.Worker.BimLib.Monitor;

/// <summary>
/// Мониторинг здоровья процессов Revit: проверка отклика, памяти, 
/// автоматическое закрытие диалогов, трекинг активных процессов.
/// </summary>
internal sealed class RevitProcessTracker(
    DialogDismisser dialogDismisser,
    ILogger<RevitProcessTracker> logger)
{
    /// <summary>Возвращает список всех активных (не завершённых) процессов Revit.
    /// Возвращённые <see cref="Process"/> экземпляры должны быть Disposed вызывающей стороной.
    /// Каждый Process из Process.GetProcessesByName требует явного Dispose для освобождения native handle.</summary>
    public IReadOnlyList<Process> GetAllRevitProcesses()
    {
        var snapshot = Process.GetProcessesByName("Revit");
        var active = new List<Process>(snapshot.Length);
        foreach (var p in snapshot)
        {
            try
            {
                if (!p.HasExited) active.Add(p);
                else p.Dispose();
            }
            catch (InvalidOperationException)
            {
                p.Dispose();
            }
        }
        return active;
    }

    public RevitProcessHealth CheckHealth(Process process)
    {
        return ProcessHealthHelper.CheckHealth(process, logger, "Revit");
    }

    /// <summary>Закрывает модальные диалоги Revit для указанного процесса. Возвращает 1, если диалоги были закрыты.</summary>
    public int DismissDialogs(int processId)
    {
        var dismissed = dialogDismisser.DismissDialogsForProcess((uint)processId);
        if (dismissed)
        {
            logger.LogInformation("Dialogs dismissed for process {ProcessId}", processId);
        }

        return dismissed ? 1 : 0;
    }
}
