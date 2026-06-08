namespace TelegramBot.BimLib.Models;

/// <summary>Результат определения версии Revit по заголовку .rvt/.rfa-файла.</summary>
public sealed record RevitDetectedVersion
{
    /// <summary>Год версии Revit (например, 2024).</summary>
    public int Year { get; init; }

    /// <summary>Отображаемое имя (например, "Autodesk Revit 2024").</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Поддерживается ли эта версия для запуска.</summary>
    public bool IsSupported { get; init; }

    /// <summary>Путь к Revit.exe для этой версии (если установлен).</summary>
    public string? ExecutablePath { get; init; }
}
