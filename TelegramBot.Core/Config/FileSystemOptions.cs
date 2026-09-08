namespace TelegramBot.Core.Config;

/// <summary>
/// Конфигурация файловой системы и путей.
/// </summary>
public sealed class FileSystemOptions
{
    /// <summary>Имя секции конфигурации в appsettings.json.</summary>
    public const string SectionName = "FileSystem";

    /// <summary>Корневая директория для файлового браузера.</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>Относительный путь к RVT-файлам внутри раздела.</summary>
    public string RvtDirectoryName { get; set; } = "01_RVT";

    /// <summary>Относительный путь к директории проекта.</summary>
    public string ProjectDirectoryName { get; set; } = "01_PROJECT";

    /// <summary>Регулярное выражение для папок разделов.</summary>
    public string SectionFolderPattern { get; set; } = @"^(\d{2}|\d{3}|I{1,3})_";

    /// <summary>
    /// Путь к директории логов. Если не задан (null или пусто),
    /// используется дефолтный путь: %USERPROFILE%\Documents\TelegramBot\Logs.
    /// </summary>
    public string? LogDirectory { get; set; }

    /// <summary>
    /// Путь к директории для TaskFile/ResultFile XML-обмена между Worker и BIM-исполнителями.
    /// Если не задан (null или пусто), используется дефолтный путь:
    /// <c>%USERPROFILE%\Documents\TelegramBot\TaskDirectory</c> — на одном уровне с <c>Logs\</c>,
    /// чтобы админ мог открыть папку вручную и проверить активные task/result XML-файлы.
    /// </summary>
    public string? TaskDirectory { get; set; }

    /// <summary>
    /// Возвращает эффективный путь к TaskDirectory: значение из конфига или дефолт.
    /// Используется Worker'ом и любым другим кодом, который пишет/читает task/result XML.
    /// </summary>
    public string GetEffectiveTaskDirectory()
    {
        return Path.GetFullPath(!string.IsNullOrWhiteSpace(TaskDirectory)
            ? TaskDirectory
            : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents",
            "TelegramBot",
            "TaskDirectory"));
    }

    /// <summary>Проверяет, что путь находится внутри указанной корневой директории.</summary>
    public static bool IsPathWithinRoot(string rootPath, string path)
    {
        try
        {
            var root = NormalizePath(rootPath);
            var candidate = NormalizePath(path);

            return candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
