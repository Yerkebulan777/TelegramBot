namespace TelegramBot.Core.Config;

/// <summary>
/// Проверяет корневой путь и приводит его к UNC-виду: пользователь отправляет букву
/// подключённого сетевого диска (<c>Z:\</c>), UNC-путь Server определяет сам.
/// </summary>
public sealed class UncRootPathValidator
{
    public bool TryValidate(string? candidate, out string normalizedPath, out string error)
    {
        normalizedPath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            error = "Укажите букву сетевого диска, например Z:\\";
            return false;
        }

        var value = candidate.Trim();
        if (value.StartsWith(@"\\?\", StringComparison.Ordinal)
            || value.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            error = "Расширенные и device-пути не поддерживаются.";
            return false;
        }

        if (!Path.IsPathRooted(value))
        {
            error = "Нужна буква сетевого диска, например Z:\\";
            return false;
        }

        string fullPath;
        try
        {
            // "Z:" без разделителя Windows трактует как текущий каталог диска.
            fullPath = Path.GetFullPath(value.EndsWith(':') ? value + Path.DirectorySeparatorChar : value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "Путь содержит недопустимые символы.";
            return false;
        }

        if (!UncPathResolver.TryResolve(fullPath, out var resolvedPath))
        {
            error = $"{fullPath} — не сетевой диск. Подключите диск к сетевой шаре под учётной записью службы Server или отправьте путь вида \\\\сервер\\шара.";
            return false;
        }

        var uncPath = resolvedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var parts = uncPath[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            error = "Сетевой путь неполный: нужны сервер и общая папка.";
            return false;
        }

        try
        {
            if (!Directory.Exists(uncPath))
            {
                error = "Папка недоступна для службы Server или не существует.";
                return false;
            }

            if (File.GetAttributes(uncPath).HasFlag(FileAttributes.ReparsePoint))
            {
                error = "Корневая папка не может быть reparse-point.";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = "Не удалось проверить доступ службы Server к папке.";
            return false;
        }

        normalizedPath = uncPath;
        return true;
    }
}
