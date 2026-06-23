namespace TelegramBot.Worker.BimLib.Models;

/// <summary>Результат проверки здоровья процесса Revit.</summary>
public sealed record RevitProcessHealth(
    RevitProcessStatus Status,
    long MemoryMb,
    TimeSpan Duration);

/// <summary>Статус здоровья процесса.</summary>
public enum RevitProcessStatus
{
    Healthy,
    NotResponding,
    Error
}


