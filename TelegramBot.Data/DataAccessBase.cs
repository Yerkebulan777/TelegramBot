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
    /// <remarks>
    /// <c>Minimum Pool Size=2</c> — держит 2 подключения «тёплыми» для снижения latency на cold-start.
    /// <c>Connection Idle Lifetime=300</c> — закрывает idle-подключения старше 5 минут.
    /// <c>Max Pool Size</c> намеренно не задан (default=100 достаточно для текущей нагрузки:
    /// Server: Parallel.ForEachAsync DOP=10 + notification; Worker: drain loop + cleanup + health).
    /// <c>Multiplexing</c> не задан (default=true в Npgsql 6+).
    /// <c>Timeout=30</c> — budget на cold-start TCP-handshake под пиковой нагрузкой (default 15s
    /// недостаточен при 5+ параллельных Revit: localhost-connect таймаутился при старте Worker).
    /// </remarks>
    public const string DefaultConnectionString =
        "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres;Timeout=30;Minimum Pool Size=2;Connection Idle Lifetime=300";

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
    protected async Task<NpgsqlConnection> CreateOpenConnectionAsync()
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        return conn;
    }
}
