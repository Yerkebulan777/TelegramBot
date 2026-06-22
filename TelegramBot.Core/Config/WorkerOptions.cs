namespace TelegramBot.Core.Config;

/// <summary>
/// Конфигурация Worker: маппинг кодов команд на исполняемые файлы, таймаут и партиции.
/// </summary>
public sealed class WorkerOptions
{
    /// <summary>Имя секции конфигурации в appsettings.json.</summary>
    public const string SectionName = "Worker";

    /// <summary>
    /// Literal dispatcher expected by RevitBIMFusion's WorkerCommand AddIn.
    /// The actual export command is read from TaskFile.commandText.
    /// </summary>
    public const string RevitDispatcherCommand = "WORKER";

    /// <summary>
    /// Максимальное время выполнения одной команды в минутах (по умолчанию 180 = 3 часа).
    /// Используется для timeout всего цикла выполнения (preparation + process start + wait),
    /// а также для расчёта Lease (ProcessTimeoutMinutes + 5 мин буфер для crash recovery).
    /// </summary>
    public int ProcessTimeoutMinutes { get; set; } = 180;

    /// <summary>Максимальное количество попыток выполнения команды (по умолчанию 5).</summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>
    /// Коды возврата процесса, которые считаются постоянными ошибками (InvalidFileError).
    /// При получении такого кода команда помечается как Failed без повторных попыток.
    /// Пустое множество (по умолчанию) — классификация только по тексту ошибки.
    /// </summary>
    public HashSet<int> PermanentFailureExitCodes { get; set; } = [];

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
    /// Партиции приоритетов: ключ — максимальный Priority threshold (чем меньше Priority, тем выше приоритет),
    /// значение — максимальное количество одновременных процессов (SemaphoreSlim).
    /// Команда попадает в первый threshold >= Priority.
    /// Конфигурация по умолчанию:
    /// Priority 0 (Critical) — 5 слотов,
    /// Priority 1 (High) — 3 слота,
    /// Priority 2 (Medium) — 2 слота,
    /// Priority 3 (Low) — 1 слот.
    /// </summary>
    public SortedDictionary<int, int> Partitions { get; set; } = new()
    {
        [0] = 5,
        [1] = 3,
        [2] = 2,
        [3] = 1,
    };

    /// <summary>
    /// Маппинг кодов команд (CommandText) на конфигурацию исполняемого файла.
    /// По умолчанию: PDF/DWG/IFC/BIMDOC/NWC → Revit.exe, CLASHREP → FileConvert.exe, AUTORES → python ai_agent.py
    /// </summary>
    public Dictionary<string, CommandConfig> Commands { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        // Шаблоны аргументов НЕ передают {FilePath} в CLI — путь к исходному файлу передаётся только в TaskFile.filePath.
        // Это гарантирует, что плагин (AddIn/wrapper) сам откроет файл с правильными OpenOptions (Audit=true, DetachFromCentral).
        // Revit AddIn ожидает fixed dispatcher WORKER в args[2]; реальный commandText берётся из TaskFile.
        // См. …\RevitBIMFusion\Docs\BimPluginContract.md §CLI Arguments.
        ["PDF"] = new() { ExecutablePath = "Revit.exe", ArgumentsTemplate = $"/command \"{RevitDispatcherCommand}\" \"{{TaskFilePath}}\"", AllowedExtensions = [".rvt", ".rfa"] },
        ["DWG"] = new() { ExecutablePath = "Revit.exe", ArgumentsTemplate = $"/command \"{RevitDispatcherCommand}\" \"{{TaskFilePath}}\"", AllowedExtensions = [".rvt", ".rfa"] },
        ["IFC"] = new() { ExecutablePath = "Revit.exe", ArgumentsTemplate = $"/command \"{RevitDispatcherCommand}\" \"{{TaskFilePath}}\"", AllowedExtensions = [".rvt", ".rfa"] },
        ["BIMDOC"] = new() { ExecutablePath = "Revit.exe", ArgumentsTemplate = $"/command \"{RevitDispatcherCommand}\" \"{{TaskFilePath}}\"", AllowedExtensions = [".rvt", ".rfa"] },
        ["NWC"] = new() { ExecutablePath = "Revit.exe", ArgumentsTemplate = $"/command \"{RevitDispatcherCommand}\" \"{{TaskFilePath}}\"", AllowedExtensions = [".rvt", ".rfa"] },
        ["CLASHREP"] = new() { ExecutablePath = "FileConvert.exe", ArgumentsTemplate = "/command \"{CommandText}\" \"{TaskFilePath}\"", AllowedExtensions = [".nwc", ".nwd", ".nwf"] },
        ["AUTORES"] = new() { ExecutablePath = "python", ArgumentsTemplate = "ai_agent.py --command \"{CommandText}\" --task \"{TaskFilePath}\"", AllowedExtensions = [".rvt", ".ifc", ".nwc"], WorkingDirectory = "." },
    };
}
