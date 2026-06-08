using TelegramBot.BimLib.Models;

namespace TelegramBot.BimLib.Interfaces;

/// <summary>Мониторинг здоровья процессов Navisworks (Roamer.exe, FileConvert.exe).</summary>
public interface INavisworksProcessTracker
{
    /// <summary>Возвращает список всех активных процессов Navisworks.</summary>
    IReadOnlyList<System.Diagnostics.Process> GetAllProcesses();

    /// <summary>Проверяет здоровье процесса Navisworks/FileConvert.</summary>
    RevitProcessHealth CheckHealth(System.Diagnostics.Process process);
}
