using Microsoft.Extensions.Logging;
using Npgsql;

namespace TelegramBot.Data;

/// <summary>
/// Базовый класс для всех сервисов доступа к данным.
/// Предоставляет общий метод создания подключения к PostgreSQL.
/// </summary>
public abstract class DataAccessBase
{
    /// <summary>Строка подключения по умолчанию.</summary>
    public const string DefaultConnectionString = "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres";

    private readonly string _connectionString;

    /// <summary>Логгер для использования в наследниках.</summary>
    protected readonly ILogger Logger;

    /// <summary>
    /// Конструктор.
    /// </summary>
    /// <param name="connectionString">Строка подключения к PostgreSQL.</param>
    /// <param name="logger">Экземпляр логгера.</param>
    protected DataAccessBase(string connectionString, ILogger logger)
    {
        _connectionString = connectionString;
        Logger = logger;
    }

    /// <summary>
    /// Создаёт и открывает подключение к PostgreSQL.
    /// </summary>
    protected Task<NpgsqlConnection> CreateOpenConnectionAsync()
        => NpgsqlHelper.CreateOpenConnectionAsync(_connectionString);
}
