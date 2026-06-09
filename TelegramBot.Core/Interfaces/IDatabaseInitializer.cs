namespace TelegramBot.Core.Interfaces;

/// <summary>
/// Service for database initialization.
/// </summary>
public interface IDatabaseInitializer
{
    /// <summary>Инициализирует схему БД.</summary>
    Task InitializeDatabaseAsync();
}
