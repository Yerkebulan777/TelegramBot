namespace TelegramBot.Core.Config;

/// <summary>Проверяет доступный Server'у корневой UNC-путь.</summary>
public sealed class UncRootPathValidator
{
    public bool TryValidate(string? candidate, out string normalizedPath, out string error)
    {
        normalizedPath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            error = "Укажите UNC-путь вида \\сервер\\шара.";
            return false;
        }

        var value = candidate.Trim();
        if (value.StartsWith(@"\\?\", StringComparison.Ordinal)
            || value.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            error = "Расширенные и device-пути не поддерживаются. Используйте \\сервер\\шара.";
            return false;
        }

        if (!value.StartsWith(@"\\", StringComparison.Ordinal))
        {
            error = "Нужен прямой UNC-путь вида \\сервер\\шара. Буквы дисков не поддерживаются.";
            return false;
        }

        var parts = value[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            error = "UNC-путь должен содержать сервер и шару: \\сервер\\шара.";
            return false;
        }

        try
        {
            normalizedPath = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "Путь содержит недопустимые символы.";
            return false;
        }

        if (!normalizedPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            error = "После нормализации путь не является UNC-путём.";
            return false;
        }

        try
        {
            if (!Directory.Exists(normalizedPath))
            {
                error = "Папка недоступна для службы Server или не существует.";
                return false;
            }

            if (File.GetAttributes(normalizedPath).HasFlag(FileAttributes.ReparsePoint))
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

        return true;
    }
}
