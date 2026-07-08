namespace TelegramBot.Core.Config;

/// <summary>
/// Конфигурация Worker: маппинг кодов команд на исполняемые файлы, таймауты и лимит параллельности.
/// </summary>
public sealed class WorkerOptions
{
    /// <summary>Имя секции конфигурации в appsettings.json.</summary>
    public const string SectionName = "Worker";

    /// <summary>
    /// Process-scoped environment variable used to pass TaskFile to RevitBIMFusion.
    /// </summary>
    public const string RevitTaskFileEnvironmentVariable = "REVITBIMFUSION_TASK_FILE";

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
    /// Интервал мониторинга active процессов в секундах (по умолчанию 30).
    /// </summary>
    public int ProcessMonitorIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Через сколько секунд NotResponding считать длительным зависанием. Минимум/шаг — 30 секунд.
    /// </summary>
    public int UnresponsiveThresholdSeconds { get; set; } = 60;

    /// <summary>
    /// Через сколько дней автоматически скрывать сессии без pending/processing команд.
    /// Значение 0 или меньше отключает автоочистку.
    /// </summary>
    public int CompletedSessionRetentionDays { get; set; } = 30;

    /// <summary>
    /// Лимит параллельных команд (по умолчанию 5).
    /// </summary>
    public int MaxConcurrentCommands { get; set; } = 5;

    /// <summary>
    /// Маппинг кодов команд (CommandText) на конфигурацию исполняемого файла.
    /// По умолчанию: PDF/DWG/NWC/DATA/IFC/BIMDOC → Revit.exe, CLASHREP → FileConvert.exe, AUTORES → python ai_agent.py
    /// </summary>
    public Dictionary<string, CommandConfig> Commands { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
