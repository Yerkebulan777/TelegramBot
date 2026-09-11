using System.Text;
using TelegramBot.Core.Config;
using TelegramBot.Core.Models;

namespace TelegramBot.Server.Helpers;

/// <summary>Renders a completion summary without database or Telegram I/O.</summary>
public static class CompletionMessageFormatter
{
    public static string Format(SessionCompletionSummary session)
    {
        var prefix = string.IsNullOrEmpty(session.ProjectName) ? "" : $"{session.ProjectName} — ";
        var durationPrefix = FormatDurationPrefix(session.DurationSeconds);

        var header = (session.FailedFiles, session.DoneFiles, session.WarnedCommands.Count) switch
        {
            (0, _, 0) => $"✅ {prefix}{durationPrefix}сессия завершена — все {session.DoneFiles} файлов обработано",
            (0, _, _) => $"⚠️ {prefix}{durationPrefix}сессия завершена — все {session.DoneFiles} файлов обработано (есть предупреждения)",
            (_, 0, _) => $"❌ {prefix}{durationPrefix}сессия завершена — все {session.FailedFiles} файлов с ошибками",
            _ => $"⚠️ {prefix}{durationPrefix}сессия завершена: {session.DoneFiles} ✅, {session.FailedFiles} ❌ из {session.TotalFiles}"
        };
        var summary = new StringBuilder(header);

        if (session.FailedFiles > 0 && session.FailedCommands.Count > 0)
        {
            AppendCommandNotes(summary, "Ошибки:", session.FailedCommands);
        }

        if (session.WarnedCommands.Count > 0)
        {
            AppendCommandNotes(summary, "Предупреждения:", session.WarnedCommands);
        }

        return ClampToTelegramLimit(summary);
    }

    /// <summary>Максимум пунктов с ошибками в одном сообщении; остальные схлопываются в «и ещё N».</summary>
    private const int MaxFailedFilesInMessage = 15;

    /// <summary>Лимит длины причины сбоя на один файл — чтобы стек/портянка не раздула сообщение.</summary>
    private const int MaxReasonLength = 200;

    /// <summary>Жёсткий лимит текста под потолок Telegram (4096) с запасом на маркер обрыва.</summary>
    private const int MaxMessageLength = 4000;

    private static void AppendCommandNotes(StringBuilder summary, string title, List<FailedCommandInfo> commands)
    {
        _=summary.Append("\n\n").Append(title).Append('\n');

        var shown = Math.Min(commands.Count, MaxFailedFilesInMessage);
        for (var i = 0; i < shown; i++)
        {
            if (i > 0)
            {
                _ = summary.AppendLine();
            }

            var command = commands[i];
            var filePath = FormatFilePath(command);
            _ = summary.Append("- ").Append(filePath)
                .Append(" [").Append(command.CommandText).Append(']')
                .Append("\n  Причина: ").Append(FormatReason(command, filePath));
        }

        if (commands.Count > MaxFailedFilesInMessage)
        {
            _ = summary.Append("\n…и ещё ").Append(commands.Count - MaxFailedFilesInMessage);
        }
    }

    /// <summary>Оставляет первую строку причины (без стека) и обрезает до <see cref="MaxReasonLength"/>.</summary>
    private static string FormatReason(FailedCommandInfo command, string filePath)
    {
        var errorMessage = command.ErrorMessage;
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return "Исполнитель не сообщил причину. Обратитесь к администратору с этим заданием.";
        }

        // Сокращаем пути до обрезки текста, чтобы длинный UNC-путь не скрывал причину.
        if (!string.IsNullOrWhiteSpace(command.FilePath))
        {
            errorMessage = errorMessage.Replace(command.FilePath, filePath, StringComparison.OrdinalIgnoreCase)
                .Replace(command.FilePath.Replace('\\', '/'), filePath, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(command.RootPath))
        {
            var root = command.RootPath.TrimEnd('\\', '/');
            errorMessage = errorMessage.Replace(root + "\\", "", StringComparison.OrdinalIgnoreCase)
                .Replace(root.Replace('\\', '/') + "/", "", StringComparison.OrdinalIgnoreCase);
        }

        errorMessage = errorMessage.Trim()
            .Replace("File validation failed for path:", "Файл не прошёл проверку пути, доступности или формата:", StringComparison.OrdinalIgnoreCase)
            .Replace("Process timed out after", "Превышено время выполнения:", StringComparison.OrdinalIgnoreCase)
            .Replace("Process exited with code", "Программа завершилась с ошибкой. Код:", StringComparison.OrdinalIgnoreCase)
            .Replace("Revit exited without writing the required ResultFile", "Revit завершился без отчёта о результате. Успешное выполнение не подтверждено.", StringComparison.OrdinalIgnoreCase)
            .Replace("Invalid plugin result file", "Не удалось прочитать результат: плагин создал некорректный файл отчёта.", StringComparison.OrdinalIgnoreCase)
            .Replace("Plugin reported failure", "Плагин сообщил об ошибке выполнения.", StringComparison.OrdinalIgnoreCase)
            .Replace("Plugin reported cancellation", "Плагин сообщил об отмене выполнения.", StringComparison.OrdinalIgnoreCase)
            .Replace("Unknown command type:", "Неизвестная команда:", StringComparison.OrdinalIgnoreCase);

        // Стек/детали обычно идут с переноса — пользователю нужна только первая строка (суть ошибки).
        var firstLine = errorMessage.AsSpan();
        var newline = firstLine.IndexOfAny('\r', '\n');
        if (newline >= 0)
        {
            firstLine = firstLine[..newline];
        }

        return firstLine.Length > MaxReasonLength
            ? $"{firstLine[..MaxReasonLength]}…"
            : firstLine.ToString();
    }

    private static string FormatFilePath(FailedCommandInfo command)
    {
        if (!string.IsNullOrWhiteSpace(command.RootPath)
            && FileSystemOptions.IsPathWithinRoot(command.RootPath, command.FilePath))
        {
            return Path.GetRelativePath(Path.GetFullPath(command.RootPath), Path.GetFullPath(command.FilePath));
        }

        // Старые задания могут не иметь сохранённого корня.
        return Path.GetFileName(command.FilePath);
    }

    /// <summary>Гарантирует, что сообщение не превысит лимит Telegram — иначе SendAsync выбросит исключение.</summary>
    private static string ClampToTelegramLimit(StringBuilder summary)
    {
        if (summary.Length <= MaxMessageLength)
        {
            return summary.ToString();
        }

        return summary.ToString(0, MaxMessageLength - 1) + "…";
    }

    private static string FormatDurationPrefix(int? durationSeconds)
    {
        return durationSeconds is > 0 ? $"{FormatDuration(durationSeconds.Value)} — " : "";
    }

    private static string FormatDuration(int totalSeconds)
    {
        var duration = TimeSpan.FromSeconds(totalSeconds);

        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours} ч {duration.Minutes:D2} мин"
            : duration.TotalMinutes >= 1 ? $"{duration.Minutes} мин {duration.Seconds:D2} с" : $"{duration.Seconds} с";
    }
}
