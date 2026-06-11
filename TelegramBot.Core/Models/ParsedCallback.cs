namespace TelegramBot.Core.Models;

/// <summary>
/// Распарсенные данные callback-запроса (префикс и аргумент).
/// </summary>
public readonly record struct ParsedCallback(string Prefix, string Argument)
{
    /// <summary>Проверяет, совпадает ли префикс с указанным значением.</summary>
    public bool Is(string prefix)
    {
        return string.Equals(Prefix, prefix, StringComparison.Ordinal);
    }

}

/// <summary>
/// Парсер данных callback-запроса.
/// </summary>
public static class CallbackDataParser
{
    /// <summary>Парсит строку данных callback в префикс и аргумент. Формат: "PREFIX:argument".</summary>
    public static ParsedCallback Parse(string callbackData)
    {
        if (string.IsNullOrEmpty(callbackData))
        {
            return new ParsedCallback(string.Empty, string.Empty);
        }

        var delimiterIndex = callbackData.IndexOf(':');
        if (delimiterIndex < 0)
        {
            return new ParsedCallback(callbackData, string.Empty);
        }

        var prefix = callbackData[..(delimiterIndex + 1)];
        var argument = delimiterIndex + 1 < callbackData.Length
            ? callbackData[(delimiterIndex + 1)..]
            : string.Empty;

        return new ParsedCallback(prefix, argument);
    }
}
