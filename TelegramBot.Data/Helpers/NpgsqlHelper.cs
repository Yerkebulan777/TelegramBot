using Npgsql;

namespace TelegramBot.Data.Helpers;

/// <summary>
/// Статический helper для создания открытых NpgsqlConnection.
/// Устраняет дублирование паттерна new NpgsqlConnection + OpenAsync между сервисами.
/// </summary>
public static class NpgsqlHelper
{
    /// <summary>
    /// Создаёт и открывает подключение к PostgreSQL.
    /// </summary>
    /// <param name="connectionString">Строка подключения PostgreSQL.</param>
    /// <param name="ct">Токен отмены (по умолчанию <see cref="CancellationToken.None"/>).</param>
    /// <returns>Открытое <see cref="NpgsqlConnection"/>.</returns>
    /// <exception cref="NpgsqlException">При ошибке подключения к серверу.</exception>
    /// <exception cref="OperationCanceledException">Если отмена была запрошена через <paramref name="ct"/>.</exception>
    public static async Task<NpgsqlConnection> CreateOpenConnectionAsync(
        string connectionString, CancellationToken ct = default)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
