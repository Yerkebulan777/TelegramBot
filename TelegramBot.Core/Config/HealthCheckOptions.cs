namespace TelegramBot.Core.Config;

/// <summary>
/// Конфигурация HTTP-сервера health checks.
/// </summary>
public sealed class HealthCheckOptions
{
    /// <summary>Имя секции конфигурации в appsettings.json.</summary>
    public const string SectionName = "HealthCheck";

    /// <summary>Порт для health check HTTP-сервера (по умолчанию 5000 для Server, 5001 для Worker).</summary>
    public int Port { get; set; } = 5000;

    /// <summary>Имя сервиса для отображения в JSON-ответе health check.</summary>
    public string ServiceName { get; set; } = "unknown";

    /// <summary>Интервал кэширования health check результатов в секундах (по умолчанию 10).
    /// Предотвращает DoS при частых опросах.</summary>
    public int CacheSeconds { get; set; } = 10;

    /// <summary>Таймаут подключения к БД для readiness check в секундах (по умолчанию 5).</summary>
    public int DbCheckTimeoutSeconds { get; set; } = 5;
}
