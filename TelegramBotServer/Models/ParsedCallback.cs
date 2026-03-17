namespace TelegramBotServer.Models;

/// <summary>
/// Represents a parsed callback data string with prefix and argument.
/// </summary>
public readonly record struct ParsedCallback(string Prefix, string Argument)
{
    /// <summary>
    /// Checks if the callback prefix matches the specified value.
    /// </summary>
    public bool Is(string prefix) => string.Equals(Prefix, prefix, StringComparison.Ordinal);

    /// <summary>
    /// Checks if the callback prefix matches any of the two specified values.
    /// </summary>
    public bool IsAny(string prefix1, string prefix2) => Is(prefix1) || Is(prefix2);
}

/// <summary>
/// Parser for callback data strings.
/// </summary>
public static class CallbackDataParser
{
    /// <summary>
    /// Parses callback data string into prefix and argument.
    /// Callback format: "PREFIX:argument" (e.g., "OPENFOLDER:abc123")
    /// </summary>
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
