namespace TelegramBot.BimLib.Models;

/// <summary>Результат определения версии Revit по заголовку .rvt/.rfa-файла.</summary>
public sealed record RevitDetectedVersion
{
    /// <summary>Год версии Revit (например, 2024).</summary>
    public int Year { get; init; }

    /// <summary>Путь к Revit.exe для этой версии (если установлен).</summary>
    public string? ExecutablePath { get; init; }
}
