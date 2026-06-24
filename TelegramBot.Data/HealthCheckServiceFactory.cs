using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TelegramBot.Core.Config;
using TelegramBot.Core.Health;

namespace TelegramBot.Data;

/// <summary>
/// Фабрика для единой настройки readiness probe health check в Server и Worker.
/// </summary>
public static class HealthCheckServiceFactory
{
    public static HealthCheckHostedService Create(
        IOptions<HealthCheckOptions> options,
        ILogger<HealthCheckHostedService> logger,
        string connectionString)
    {
        return new HealthCheckHostedService(options, logger)
        {
            DatabaseCheckAsync = async ct =>
            {
                await using var conn = await NpgsqlHelper.CreateOpenConnectionAsync(connectionString, ct);
                _ = await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: ct));
                return true;
            },
        };
    }
}
