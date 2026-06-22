namespace TelegramBot.Core.Config;

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

    /// <summary>
    /// Путь к директории логов. Если не задан (null или пусто),
    /// используется дефолтный путь: %USERPROFILE%\Documents\TelegramBot\Logs.
    /// </summary>
    public string? LogDirectory { get; set; }

    /// <summary>
    /// Путь к директории для TaskFile/ResultFile JSON-обмена между Worker и BIM-исполнителями.
    /// Если не задан (null или пусто), используется дефолтный путь:
    /// <c>%USERPROFILE%\Documents\TelegramBot\TaskDirectory</c> — на одном уровне с <c>Logs\</c>,
    /// чтобы админ мог открыть папку вручную и проверить активные task/result JSON-файлы.
    /// </summary>
    public string? TaskDirectory { get; set; }

    /// <summary>
    /// Возвращает эффективный путь к TaskDirectory: значение из конфига или дефолт.
    /// Используется Worker'ом и любым другим кодом, который пишет/читает task/result JSON.
    /// </summary>
    public string GetEffectiveTaskDirectory()
    {
        if (!string.IsNullOrWhiteSpace(TaskDirectory))
        {
            return TaskDirectory;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents",
            "TelegramBot",
            "TaskDirectory");
    }

    /// <summary>Возвращает полный путь к RVT-директории для раздела.</summary>
    public string GetRvtPath(string sectionPath)
    {
        return Path.Combine(sectionPath, RvtDirectoryName);
    }

    /// <summary>Проверяет, является ли файл файлом Revit.</summary>
    public bool IsRevitFile(string filePath)
    {
        return filePath.EndsWith(RevitFileExtension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Проверяет, находится ли пользователь на уровне выбора проектов (а не разделов).</summary>
    public bool IsAtProjectLevel(string currentPath)
    {
        return !string.Equals(Path.GetFileName(currentPath), ProjectDirectoryName,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Проверяет, что путь находится внутри корневой директории.</summary>
    public bool IsPathWithinRoot(string path)
    {
        try
        {
            var root = Path.GetFullPath(RootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
