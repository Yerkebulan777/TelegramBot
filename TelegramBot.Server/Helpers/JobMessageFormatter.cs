using System.Text;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;

namespace TelegramBot.Server.Helpers;

/// <summary>
/// Форматирование Markdown-сообщений пользователю (представление, не бизнес-логика).
/// Выделено из <c>SlashCommandService</c>: чистая сборка строк через StringBuilder.
/// </summary>
public static class JobMessageFormatter
{
    public static string BuildJobQueuedMessage(
        IReadOnlyList<string> commandNames,
        string projectName,
        IEnumerable<string> sectionNames,
        IReadOnlyList<string> queuedFilePaths,
        IReadOnlyList<CommandConflict>? skippedPairs = null)
    {
        var builder = new StringBuilder()
            .AppendLine("✅ *Задание успешно добавлено в очередь*")
            .AppendLine()
            .AppendLine("🧰 *Команды*");

        foreach (var commandName in commandNames)
        {
            _=builder.AppendLine($"• {MarkdownHelper.Escape(commandName)}");
        }

        _=builder
            .AppendLine()
            .AppendLine("📌 *Проект*")
            .AppendLine($"`{MarkdownHelper.Escape(projectName)}`")
            .AppendLine()
            .AppendLine("📂 *Разделы*");

        foreach (var sectionName in sectionNames)
        {
            if (builder.Length + sectionName.Length * 2 > 900)
            {
                _ = builder.AppendLine("• Остальные разделы — в /status");
                break;
            }
            _=builder.AppendLine($"• {MarkdownHelper.Escape(sectionName)}");
        }

        _=builder
            .AppendLine()
            .AppendLine("📄 *Файлы*");

        var shownFiles = 0;
        foreach (var filePath in queuedFilePaths)
        {
            var line = $"• {MarkdownHelper.Escape(Path.GetFileName(filePath))}";
            if (builder.Length + line.Length > 1400)
            {
                break;
            }
            _ = builder.AppendLine(line);
            shownFiles++;
        }
        if (shownFiles < queuedFilePaths.Count)
        {
            _ = builder.AppendLine($"Ещё файлов: {queuedFilePaths.Count - shownFiles}. Полный список: /status");
        }

        if (skippedPairs is { Count: > 0 })
        {
            _=builder.AppendLine().Append(BuildConflictsMessage(skippedPairs));
        }

        return builder.ToString();
    }

    public static string BuildConflictsMessage(IReadOnlyList<CommandConflict> conflicts)
    {
        const int maxDetails = 8;
        var shown = 0;
        var builder = new StringBuilder()
            .AppendLine($"⚠️ *Повторно не добавлено операций:* `{conflicts.Count}`");
        foreach (var conflict in conflicts.Take(maxDetails))
        {
            var line = new StringBuilder($"• {MarkdownHelper.Escape(Path.GetFileName(conflict.FilePath))} — {MarkdownHelper.Escape(conflict.Command)}");
            if (conflict.SessionId is { } sessionId && conflict.CreatedAt is { } createdAt)
            {
                var status = conflict.Status == Statuses.Processing ? "выполняется по данным очереди" : "ожидает запуска";
                _ = line.Append($": задание #{sessionId}, операция #{conflict.CommandId}, {status}; добавлено {createdAt:dd.MM.yyyy HH:mm} UTC");
            }
            else
            {
                _ = line.Append(": совпадение при добавлении; прежняя операция уже изменила статус");
            }
            if (builder.Length + line.Length > 1800)
            {
                break;
            }
            _ = builder.AppendLine(line.ToString());
            shown++;
        }
        if (conflicts.Count > shown)
        {
            _ = builder.AppendLine($"Ещё операций: {conflicts.Count - shown}. Состояние заданий: /status");
        }
        return builder.ToString();
    }
}
