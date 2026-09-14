using System.Text;
using TelegramBot.Core.Models;

namespace TelegramBot.Server.Helpers;

/// <summary>Renders a completion summary without database or Telegram I/O.</summary>
public static class CompletionMessageFormatter
{
    /// <summary>Максимум имён файлов или пунктов ошибки в одном сообщении; остальные схлопываются в «и ещё N».</summary>
    private const int MaxItemsInMessage = 15;

    /// <summary>Лимит длины причины сбоя на один файл — чтобы стек/портянка не раздула сообщение.</summary>
    private const int MaxReasonLength = 200;

    /// <summary>Жёсткий лимит текста под потолок Telegram (4096) с запасом на маркер обрыва.</summary>
    private const int MaxMessageLength = 4000;

    public static string Format(SessionCompletionSummary session)
    {
        var failed = session.Failed.ToList();
        var warned = session.Warned.ToList();
        var summary = new StringBuilder();
        AppendJobLines(summary, session);

        var duration = session.DurationSeconds is > 0 ? $" ({FormatDuration(session.DurationSeconds.Value)})" : "";
        _ = summary.Append('\n').Append(StatusLine(failed.Count > 0, warned.Count > 0)).Append(duration);
        AppendNotes(summary, "Ошибка:", failed);
        AppendNotes(summary, "Предупреждение:", warned);
        return ClampToTelegramLimit(summary);
    }

    private static string StatusLine(bool hasErrors, bool hasWarnings) =>
        hasErrors ? "❌ есть ошибки" : hasWarnings ? "⚠️ есть предупреждения" : "✅ выполнено без ошибок";

    private static void AppendJobLines(StringBuilder summary, SessionCompletionSummary session)
    {
        var commands = new List<string>();
        var commandSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileNames = new List<string>();
        var fileSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var command in session.Commands)
        {
            if (!string.IsNullOrWhiteSpace(command.CommandText) && commandSeen.Add(command.CommandText))
            {
                commands.Add(command.CommandText);
            }

            var fileName = Path.GetFileName(command.FilePath);
            if (!string.IsNullOrWhiteSpace(fileName) && fileSeen.Add(fileName))
            {
                fileNames.Add(fileName);
            }
        }

        var project = string.IsNullOrWhiteSpace(session.ProjectName) ? "—" : session.ProjectName.Trim();
        var commandText = commands.Count == 0 ? "—" : string.Join(", ", commands);
        _ = summary.Append(commandText).Append('\n').Append(project);
        AppendCappedLines(summary, fileNames.Count == 0 ? ["—"] : fileNames);
    }

    private static void AppendNotes(StringBuilder summary, string title, List<SessionCommandInfo> commands)
    {
        if (commands.Count == 0)
        {
            return;
        }

        _ = summary.Append('\n').Append(title);
        var shown = Math.Min(commands.Count, MaxItemsInMessage);
        var showCommand = commands
            .Select(command => command.CommandText)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() > 1;

        for (var i = 0; i < shown; i++)
        {
            var command = commands[i];
            var fileName = Path.GetFileName(command.FilePath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = command.FilePath;
            }

            _ = summary.Append('\n').Append(fileName);
            if (showCommand && !string.IsNullOrWhiteSpace(command.CommandText))
            {
                _ = summary.Append(" [").Append(command.CommandText).Append(']');
            }

            _ = summary.Append(": ").Append(FormatReason(command));
        }

        if (commands.Count > MaxItemsInMessage)
        {
            _ = summary.Append("\n…и ещё ").Append(commands.Count - MaxItemsInMessage);
        }
    }

    private static void AppendCappedLines(StringBuilder summary, List<string> lines)
    {
        var shown = Math.Min(lines.Count, MaxItemsInMessage);
        for (var i = 0; i < shown; i++)
        {
            _ = summary.Append('\n').Append(lines[i]);
        }

        if (lines.Count > MaxItemsInMessage)
        {
            _ = summary.Append("\n…и ещё ").Append(lines.Count - MaxItemsInMessage);
        }
    }

    /// <summary>Оставляет первую строку причины (без стека) и обрезает до <see cref="MaxReasonLength"/>.</summary>
    private static string FormatReason(SessionCommandInfo command)
    {
        var errorMessage = command.ErrorMessage;
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return "Исполнитель не сообщил причину. Обратитесь к администратору с этим заданием.";
        }

        // Сокращаем пути до обрезки текста, чтобы длинный UNC-путь не скрывал причину.
        if (!string.IsNullOrWhiteSpace(command.FilePath))
        {
            var replacement = Path.GetFileName(command.FilePath);
            if (string.IsNullOrWhiteSpace(replacement))
            {
                replacement = command.FilePath;
            }

            errorMessage = errorMessage.Replace(command.FilePath, replacement, StringComparison.OrdinalIgnoreCase)
                .Replace(command.FilePath.Replace('\\', '/'), replacement, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>Гарантирует, что сообщение не превысит лимит Telegram — иначе SendAsync выбросит исключение.</summary>
    private static string ClampToTelegramLimit(StringBuilder summary)
    {
        if (summary.Length <= MaxMessageLength)
        {
            return summary.ToString();
        }

        return summary.ToString(0, MaxMessageLength - 1) + "…";
    }

    private static string FormatDuration(int totalSeconds)
    {
        var duration = TimeSpan.FromSeconds(totalSeconds);

        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours} ч {duration.Minutes:D2} мин"
            : duration.TotalMinutes >= 1 ? $"{duration.Minutes} мин {duration.Seconds:D2} с" : $"{duration.Seconds} с";
    }
}
