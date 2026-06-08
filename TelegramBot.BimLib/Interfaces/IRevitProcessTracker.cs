using System.Diagnostics;
using TelegramBot.BimLib.Models;

namespace TelegramBot.BimLib.Interfaces;

/// <summary>Мониторинг здоровья процессов Revit.</summary>
public interface IRevitProcessTracker
{
    /// <summary>Возвращает список всех активных процессов Revit.</summary>
    IReadOnlyList<Process> GetAllRevitProcesses();

    /// <summary>Проверяет здоровье указанного процесса.</summary>
    RevitProcessHealth CheckHealth(Process process);

    /// <summary>Закрывает диалоговые окна для указанного процесса. Возвращает количество закрытых.</summary>
    int DismissDialogs(int processId);
}
