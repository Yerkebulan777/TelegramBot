namespace TelegramBotServer.Extensions;

/// <summary>
/// Provides validation extension methods.
/// </summary>
public static class ValidationExtensions
{
    /// <summary>
    /// Validates that the ID is a positive integer.
    /// </summary>
    /// <param name="id">The ID to validate.</param>
    /// <returns>True if the ID is valid (positive).</returns>
    public static bool IsValidId(this int id) => id > 0;

    /// <summary>
    /// Validates that the ID is a positive long.
    /// </summary>
    /// <param name="id">The ID to validate.</param>
    /// <returns>True if the ID is valid (positive).</returns>
    public static bool IsValidId(this long id) => id > 0;

    /// <summary>
    /// Validates that the string is not null, empty, or whitespace.
    /// </summary>
    /// <param name="value">The string to validate.</param>
    /// <returns>True if the string has content.</returns>
    public static bool HasContent(this string? value) => !string.IsNullOrWhiteSpace(value);
}
