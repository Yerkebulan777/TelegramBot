namespace TelegramBot.Server.Helpers;

/// <summary>
/// Helper methods for escaping text for Telegram's Markdown parse modes.
/// </summary>
public static class MarkdownHelper
{
    /// <summary>
    /// Escapes special characters for <see cref="Telegram.Bot.Types.Enums.ParseMode.MarkdownV2"/>.
    /// MarkdownV2 requires escaping: \ _ * [ ] ( ) ~ ` > # + - = | { } . !
    /// </summary>
    public static string EscapeMarkdownV2(string text)
    {
        return text
            .Replace("\\", "\\\\")
            .Replace("_", "\\_")
            .Replace("*", "\\*")
            .Replace("[", "\\[")
            .Replace("]", "\\]")
            .Replace("(", "\\(")
            .Replace(")", "\\)")
            .Replace("~", "\\~")
            .Replace("`", "\\`")
            .Replace(">", "\\>")
            .Replace("#", "\\#")
            .Replace("+", "\\+")
            .Replace("-", "\\-")
            .Replace("=", "\\=")
            .Replace("|", "\\|")
            .Replace("{", "\\{")
            .Replace("}", "\\}")
            .Replace(".", "\\.")
            .Replace("!", "\\!");
    }

    /// <summary>
    /// Escapes special characters for <see cref="Telegram.Bot.Types.Enums.ParseMode.Markdown"/>.
    /// Regular Markdown requires escaping: \ _ * [ ] ( ) `
    /// </summary>
    public static string EscapeMarkdown(string text)
    {
        return text
            .Replace("\\", "\\\\")
            .Replace("_", "\\_")
            .Replace("*", "\\*")
            .Replace("[", "\\[")
            .Replace("]", "\\]")
            .Replace("(", "\\(")
            .Replace(")", "\\)")
            .Replace("`", "\\`");
    }
}
