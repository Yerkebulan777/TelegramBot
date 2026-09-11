namespace TelegramBot.Core.Models;

/// <summary>
/// Какой глобальный launch gate сериализует запуск процесса команды.
/// Revit и AutoCAD выдерживают независимые интервалы между `Process.Start()`.
/// </summary>
public enum ProcessLaunchGateKind
{
    None,
    Revit,
    AutoCad,
}
