namespace TelegramBot.Core.Config;

/// <summary>
/// Конфигурация Worker: маппинг кодов команд на исполняемые файлы, таймаут и партиции.
/// </summary>
public sealed class WorkerOptions
{
    /// <summary>Имя секции конфигурации в appsettings.json.</summary>
    public const string SectionName = "Worker";

    /// <summary>Максимальное время выполнения одной команды в секундах (по умолчанию 3 часа).</summary>
    public int ProcessTimeoutSeconds { get; set; } = 10_800;

    /// <summary>Максимальное количество попыток выполнения команды (по умолчанию 5).</summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>
    /// Базовая задержка перед первой retry в секундах (по умолчанию 60 сек).
    /// Задержка растёт экспоненциально: base * 2^(attempt - 1).
    /// Пример: 60s, 120s, 240s, 480s, 960s.
    /// </summary>
    public int RetryDelayBaseSeconds { get; set; } = 60;

    /// <summary>
    /// Партиции (приоритетные уровни). Ключ — минимальный Priority команды,
    /// значение — максимальное количество одновременных процессов для этого уровня.
    /// Команда с Priority >= threshold попадает в соответствующую партицию.
    /// Пример: { [80] = 5, [40] = 3, [0] = 1 } — высокий приоритет (>=80) имеет 5 слотов,
    /// средний (>=40) — 3, низкий (<40) — 1.
    /// </summary>
    public SortedDictionary<int, int> Partitions { get; set; } = new()
    {
        [80] = 5,
        [40] = 3,
        [0] = 1,
    };

    /// <summary>Через сколько дней удалять (soft-delete) отменённые команды (по умолчанию 7).</summary>
    public int CleanupOlderThanDays { get; set; } = 7;

    /// <summary>
    /// Маппинг кодов команд (CommandText) на конфигурацию исполняемого файла.
    /// По умолчанию: PDF/DWG/IFC/BIMDOC → Revit.exe, NWC/CLASHREP → FileConvert.exe, AUTORES → python ai_agent.py
    /// </summary>
    public Dictionary<string, CommandConfig> Commands { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PDF"] = new() { ExecutablePath = "Revit.exe", ArgumentsTemplate = "/command \"{CommandText}\" \"{FilePath}\"", AllowedExtensions = [".rvt", ".rfa"] },
        ["DWG"] = new() { ExecutablePath = "Revit.exe", ArgumentsTemplate = "/command \"{CommandText}\" \"{FilePath}\"", AllowedExtensions = [".rvt", ".rfa"] },
        ["IFC"] = new() { ExecutablePath = "Revit.exe", ArgumentsTemplate = "/command \"{CommandText}\" \"{FilePath}\"", AllowedExtensions = [".rvt", ".rfa"] },
        ["BIMDOC"] = new() { ExecutablePath = "Revit.exe", ArgumentsTemplate = "/command \"{CommandText}\" \"{FilePath}\"", AllowedExtensions = [".rvt", ".rfa"] },
        ["NWC"] = new() { ExecutablePath = "FileConvert.exe", ArgumentsTemplate = "/command \"{CommandText}\" \"{FilePath}\"", AllowedExtensions = [".nwc", ".nwd", ".nwf"] },
        ["CLASHREP"] = new() { ExecutablePath = "FileConvert.exe", ArgumentsTemplate = "/command \"{CommandText}\" \"{FilePath}\"", AllowedExtensions = [".nwc", ".nwd", ".nwf"] },
        ["AUTORES"] = new() { ExecutablePath = "python", ArgumentsTemplate = "ai_agent.py --command \"{CommandText}\" --file \"{FilePath}\"", AllowedExtensions = [".rvt", ".ifc", ".nwc"], WorkingDirectory = "." },
    };
}
