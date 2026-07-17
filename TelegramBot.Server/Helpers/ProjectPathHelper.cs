namespace TelegramBot.Server.Helpers;

/// <summary>
/// Навигация по структуре папок проекта для вывода человекочитаемых имён.
/// Выделено из <c>SlashCommandService</c>: логика обхода каталогов вверх от файла
/// до папки <c>01_PROJECT</c> не относится к оркестрации команд.
/// </summary>
/// <remarks>
/// Конвенция каталогов: проект содержит папку <paramref name="projectDirectoryName"/>
/// (обычно «01_PROJECT»), внутри которой лежат разделы, а внутри разделов — RVT-файлы.
/// </remarks>
public static class ProjectPathHelper
{
    /// <summary>Имя папки проекта (родитель папки <paramref name="projectDirectoryName"/>) для выбранного файла.</summary>
    public static string GetProjectName(string filePath, string projectDirectoryName)
    {
        var dir = Path.GetDirectoryName(filePath);
        while (!string.IsNullOrEmpty(dir))
        {
            if (string.Equals(Path.GetFileName(dir), projectDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                var parent = Path.GetDirectoryName(dir);
                return parent != null ? GetSafePathName(parent) : GetSafePathName(dir);
            }

            dir = Path.GetDirectoryName(dir);
        }

        return GetSafePathName(filePath);
    }

    /// <summary>Имя папки раздела (родитель которой — <paramref name="projectDirectoryName"/>) для выбранного файла, либо null.</summary>
    public static string? GetSectionFolderName(string filePath, string projectDirectoryName)
    {
        var dir = Path.GetDirectoryName(filePath);
        while (!string.IsNullOrEmpty(dir))
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent != null && string.Equals(Path.GetFileName(parent), projectDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                return GetSafePathName(dir);
            }

            dir = parent;
        }

        return null;
    }

    /// <summary>Возвращает последний сегмент пути; для корневых путей без имени — сам путь.</summary>
    public static string GetSafePathName(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }
}
