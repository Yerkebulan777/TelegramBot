using System.Text.RegularExpressions;
using Telegram.Bot.Types.Enums;

namespace TelegramBot.Server.Helpers;

/// <summary>
/// Helper methods for escaping text for Telegram's Markdown parse modes.
/// </summary>
public static class MarkdownHelper
{
    // Compiled Regex for MarkdownV2: \ _ * [ ] ( ) ~ ` > # + - = | { } . !
    private static readonly Regex MarkdownV2Regex = new(
        @"[\\_*\[\]()~`>#+\-=|{}.!]", RegexOptions.Compiled);

    // Compiled Regex for regular Markdown: \ _ * [ ] ( ) `
    private static readonly Regex MarkdownRegex = new(
        @"[\\_*\[\]()`]", RegexOptions.Compiled);

    /// <summary>
    /// Escapes special characters for Telegram's Markdown parse modes.
    /// Uses compiled Regex for better performance (reduces allocations vs string.Replace chain).
    /// </summary>
    /// <param name="text">Text to escape.</param>
    /// <param name="mode">Target parse mode (Markdown or MarkdownV2).</param>
    /// <returns>Escaped text safe for Telegram Markdown formatting.</returns>
    public static string Escape(string? text, ParseMode mode = ParseMode.Markdown)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var regex = mode == ParseMode.MarkdownV2 ? MarkdownV2Regex : MarkdownRegex;
        return regex.Replace(text, "\\$&");
    }
}
