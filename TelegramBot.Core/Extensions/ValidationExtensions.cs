namespace TelegramBot.Core.Extensions;

/// <summary>Методы расширения для валидации.</summary>
public static class ValidationExtensions
{
    /// <summary>Проверяет, что ID положительный.</summary>
    public static bool IsValidId(this int id) => id > 0;

    /// <summary>Проверяет, что ID положительный.</summary>
    public static bool IsValidId(this long id) => id > 0;
}
