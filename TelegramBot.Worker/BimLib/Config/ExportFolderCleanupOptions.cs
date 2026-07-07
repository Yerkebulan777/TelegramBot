namespace TelegramBot.Worker.BimLib.Config;

/// <summary>Настройки очистки папок экспорта от дублей и архивации.</summary>
public sealed class ExportFolderCleanupOptions
{
    public const string SectionName = "ExportFolderCleanup";

    /// <summary>Количество дней, после которого файл считается старым (по умолчанию 100).</summary>
    public int OldFileThresholdDays { get; set; } = 100;

    /// <summary>Порог размера в байтах для архивации старых дублей (по умолчанию 30 МБ).</summary>
    public long ArchiveSizeThresholdBytes { get; set; } = 30L * 1024 * 1024;

    /// <summary>Сколько самых свежих файлов оставлять среди старых дублей (по умолчанию 3).</summary>
    public int KeepLastCount { get; set; } = 3;

    /// <summary>Имя папки архива (по умолчанию "#_АРХИВ").</summary>
    public string ArchiveFolderName { get; set; } = "#_АРХИВ";

    /// <summary>Подпапки экспорта для очистки и соответствующие расширения.</summary>
    public Dictionary<string, string> ExportFolderMap { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["03_PDF"] = ".pdf",
        ["02_DWG"] = ".dwg",
    };
}
