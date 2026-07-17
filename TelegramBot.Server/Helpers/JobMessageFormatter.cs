using System.Text;

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
        int fileCount,
        IReadOnlyList<(string Command, string FilePath)>? skippedPairs = null)
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
            _=builder.AppendLine($"• {MarkdownHelper.Escape(sectionName)}");
        }

        _=builder
            .AppendLine()
            .AppendLine($"📄 *Количество файлов:* `{fileCount}`");

        if (skippedPairs is { Count: > 0 })
        {
            _=builder
                .AppendLine()
                .AppendLine($"⚠️ *Уже в очереди, пропущено:* `{skippedPairs.Count}`");

            foreach (var (command, filePath) in skippedPairs)
            {
                _=builder.AppendLine($"• {MarkdownHelper.Escape(Path.GetFileName(filePath))} — {MarkdownHelper.Escape(command)}");
            }
        }

        return builder.ToString();
    }
}
