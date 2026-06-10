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

    /// <summary>
    /// Максимальное время выполнения одной команды в минутах (по умолчанию 180 = 3 часа).
    /// Используется для timeout всего цикла выполнения (preparation + process start + wait).
    /// </summary>
    public int ProcessTimeoutMinutes { get; set; } = 180;

    /// <summary>Максимальное количество попыток выполнения команды (по умолчанию 5).</summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>
    /// Базовая задержка перед первой retry в секундах (по умолчанию 60 сек).
    /// Задержка растёт экспоненциально: base * 2^(attempt - 1).
    /// Пример: 60s, 120s, 240s, 480s, 960s.
    /// </summary>
    public int RetryDelayBaseSeconds { get; set; } = 60;

    /// <summary>
    /// Интервал fallback-поллинга новых задач в секундах (по умолчанию 300 = 5 минут).
    /// Используется только при потере соединения с LISTEN/NOTIFY.
    /// </summary>
    public int FallbackPollingIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Интервал фоновой очистки истёкших lease в секундах (по умолчанию 300 = 5 минут).
    /// </summary>
    public int CleanupIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Интервал проверки здоровья active процессов в секундах (по умолчанию 30).
    /// </summary>
    public int HealthCheckIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Через сколько дней автоматически скрывать сессии без pending/processing команд.
    /// Значение 0 или меньше отключает автоочистку.
    /// </summary>
    public int CompletedSessionRetentionDays { get; set; } = 30;

    /// <summary>
    /// Партиции: ключ — максимальный Priority threshold (чем меньше Priority, тем выше приоритет),
    /// значение — максимальное количество одновременных процессов.
    /// Команда попадает в первый threshold >= Priority.
    /// Пример: { [1] = 3, [2] = 5, [3] = 3, [4] = 1, [5] = 1 } —
    /// Priority 1 (Critical) — 3 слота, Priority 2 — 5 слотов, Priority 5+ — fallback в последний threshold.
    /// </summary>
    public SortedDictionary<int, int> Partitions { get; set; } = new()
    {
        [1] = 3,
        [2] = 5,
        [3] = 3,
        [4] = 1,
        [5] = 1,
    };

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
