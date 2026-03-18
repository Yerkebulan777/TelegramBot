namespace TelegramBotServer.Config;

/// <summary>
/// Конфигурация файловой системы и путей.
/// </summary>
public sealed class FileSystemOptions
{
    /// <summary>Имя секции конфигурации в appsettings.json.</summary>
    public const string SectionName = "FileSystem";

    /// <summary>Корневая директория для файлового браузера.</summary>
    public required string RootPath { get; set; }

    /// <summary>Относительный путь к RVT-файлам внутри раздела.</summary>
    public string RvtDirectoryName { get; set; } = "01_RVT";

    /// <summary>Относительный путь к директории проекта.</summary>
    public string ProjectDirectoryName { get; set; } = "01_PROJECT";

    /// <summary>Расширение файлов Revit (с точкой).</summary>
    public string RevitFileExtension { get; set; } = ".rvt";

    /// <summary>Регулярное выражение для папок разделов.</summary>
    public string SectionFolderPattern { get; set; } = @"^(\d{2}|\d{3}|I{1,3})_";

    /// <summary>Регулярное выражение для разделов с римской цифрой III.</summary>
    public string RomanThreePattern { get; set; } = @"^III_";

    /// <summary>Возвращает полный путь к RVT-директории для раздела.</summary>
    public string GetRvtPath(string sectionPath) => Path.Combine(sectionPath, RvtDirectoryName);

    /// <summary>Возвращает полный путь к директории проекта.</summary>
    public string GetProjectPath(string basePath) => Path.Combine(basePath, ProjectDirectoryName);

    /// <summary>Проверяет, является ли файл файлом Revit.</summary>
    public bool IsRevitFile(string filePath) => filePath.EndsWith(RevitFileExtension, StringComparison.OrdinalIgnoreCase);
}
