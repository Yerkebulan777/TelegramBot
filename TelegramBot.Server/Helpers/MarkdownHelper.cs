using System.Text.RegularExpressions;
using Telegram.Bot.Types.Enums;

namespace TelegramBot.Server.Helpers;

/// <summary>
/// Helper methods for escaping text for Telegram's Markdown parse modes.
/// </summary>
public static partial class MarkdownHelper
{
    [GeneratedRegex(@"[\\_*\[\]()~`>#+\-=|{}.!]")]
    private static partial Regex MarkdownV2Regex();

    [GeneratedRegex(@"[\\_*\[\]()`]")]
    private static partial Regex MarkdownRegex();

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

        var regex = mode == ParseMode.MarkdownV2 ? MarkdownV2Regex() : MarkdownRegex();
        return regex.Replace(text, "\\$&");
    }
}
