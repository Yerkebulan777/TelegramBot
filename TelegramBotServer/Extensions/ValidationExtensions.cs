namespace TelegramBotServer.Extensions;

/// <summary>Методы расширения для валидации.</summary>
public static class ValidationExtensions
{
    /// <summary>Проверяет, что ID положительный.</summary>
    public static bool IsValidId(this int id) => id > 0;

    /// <summary>Проверяет, что ID положительный.</summary>
    public static bool IsValidId(this long id) => id > 0;

    /// <summary>Проверяет, что строка не пустая.</summary>
    public static bool HasContent(this string? value) => !string.IsNullOrWhiteSpace(value);
}
