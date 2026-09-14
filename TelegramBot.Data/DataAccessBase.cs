using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace TelegramBot.Data;

/// <summary>
/// Базовый класс для всех сервисов доступа к данным.
/// Предоставляет общий метод создания подключения к PostgreSQL.
/// </summary>
public abstract class DataAccessBase
{
    /// <summary>
    /// Резолвит строку подключения к Postgres из конфигурации.
    /// При отсутствии <c>ConnectionStrings:Postgres</c> бросает — fail-fast лучше тихого падения на
    /// захардкоженную localhost-строку с паролем «postgres» (опечатка в ключе конфига иначе маскируется).
    /// Dev-значение должно лежать в <c>appsettings.Development.json</c> явно.
    /// </summary>
    /// <remarks>
    /// Рекомендуемые параметры строки (см. appsettings.json):
    /// <c>Minimum Pool Size=2</c> — держит 2 подключения «тёплыми» для снижения latency на cold-start;
    /// <c>Connection Idle Lifetime=300</c> — закрывает idle-подключения старше 5 минут;
    /// <c>Max Pool Size</c> не задаётся (default=100 достаточно: Server DOP=10 + notification, Worker drain+cleanup+health);
    /// <c>Multiplexing</c> не задаётся (в Npgsql 6+ default=false, opt-in; session advisory locks с multiplexing несовместимы);
    /// <c>Timeout=30</c> — budget на cold-start TCP-handshake под пиковой нагрузкой (default 15s
    /// недостаточен при 5+ параллельных Revit: localhost-connect таймаутился при старте Worker).
    /// </remarks>
    public static string ResolveConnectionString(IConfiguration configuration)
    {
        return configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Postgres is not configured. Set it in appsettings.json " +
                "(or environment/appsettings.Development.json) — implicit localhost fallback was removed.");
    }

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
    protected Task<NpgsqlConnection> CreateOpenConnectionAsync(CancellationToken cancellationToken = default) =>
        OpenConnectionAsync(_connectionString, cancellationToken);

    internal static async Task<NpgsqlConnection> OpenConnectionAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var conn = new NpgsqlConnection(connectionString);
        try
        {
            await conn.OpenAsync(cancellationToken);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }
}
