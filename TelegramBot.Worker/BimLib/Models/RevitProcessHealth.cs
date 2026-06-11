namespace TelegramBot.Worker.BimLib.Models;

/// <summary>Результат проверки здоровья процесса Revit.</summary>
public sealed record RevitProcessHealth(
    int ProcessId,
    RevitProcessStatus Status,
    long MemoryMb,
    TimeSpan Duration,
    bool Responding);

/// <summary>Статус здоровья процесса.</summary>
public enum RevitProcessStatus
{
    Healthy,
    NotResponding,
    Error
}


