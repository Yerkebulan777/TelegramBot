using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TelegramBot.Core.Config;

namespace TelegramBot.Core.Health;

/// <summary>
/// Background service, запускающий минимальный HTTP-сервер health checks на основе <see cref="TcpListener"/>.
/// Предоставляет три эндпоинта:
/// <list type="bullet">
///   <item><c>/health/live</c> — liveness: процесс жив (всегда 200).</item>
///   <item><c>/health/ready</c> — readiness: 200 если БД доступна, 503 если нет.</item>
///   <item><c>/health</c> — подробный JSON-отчёт со всеми проверками.</item>
/// </list>
/// </summary>
public sealed class HealthCheckHostedService(
    IOptions<HealthCheckOptions> options,
    ILogger<HealthCheckHostedService> logger)
    : BackgroundService
{
    private readonly HealthCheckOptions _options = options.Value;
    private readonly string _healthEndpoint = $"/health";
    private readonly string _liveEndpoint = $"/health/live";
    private readonly string _readyEndpoint = $"/health/ready";

    // Кэш результатов health check для предотвращения DoS
    private HealthCheckResult? _cachedResult;
    private DateTime _lastCheckTime = DateTime.MinValue;

    /// <summary>
    /// Функция, выполняющая проверку БД для readiness probe.
    /// Устанавливается при регистрации сервиса в DI.
    /// </summary>
    public Func<CancellationToken, Task<bool>>? DatabaseCheckAsync { get; set; }

    /// <summary>
    /// Дополнительные проверки для расширенного отчёта /health.
    /// Ключ — название проверки, значение — асинхронная функция, возвращающая true/false.
    /// </summary>
    public Dictionary<string, Func<CancellationToken, Task<HealthComponentStatus>>> AdditionalChecks { get; set; } = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = _options.Port;

        var listener = new TcpListener(IPAddress.Loopback, port);

        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Health check server failed to start on port {Port}", port);
            return;
        }

        logger.LogInformation(
            "Health check ready: http://localhost:{Port}/health | /live | /ready  ({Service})",
            port, _options.ServiceName);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient? client = null;

                try
                {
                    client = await listener.AcceptTcpClientAsync(stoppingToken);
                    _ = HandleClientAsync(client, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error accepting health check connection");
                    client?.Dispose();
                }
            }
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }

        logger.LogInformation("Health check server stopped");
    }

    /// <summary>Обрабатывает одно HTTP-подключение.</summary>
    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var buffer = new byte[4096];
                var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct);

                if (bytesRead == 0)
                {
                    return;
                }

                var request = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                var requestLine = ParseHttpRequestLine(request);

                var (statusCode, contentType, body) = await ServeAsync(requestLine, ct);
                var response = BuildHttpResponse(statusCode, contentType, body);

                await stream.WriteAsync(Encoding.UTF8.GetBytes(response), ct);
                await stream.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error handling health check request");
        }
    }

    /// <summary>Извлекает метод и путь из HTTP-запроса.</summary>
    private static HealthHttpRequestLine ParseHttpRequestLine(string request)
    {
        var lineEnd = request.IndexOf("\r\n", StringComparison.Ordinal);
        var firstLine = lineEnd >= 0 ? request[..lineEnd] : request;
        var parts = firstLine.Split(' ');
        var method = parts.Length >= 1 ? parts[0] : "";
        var path = parts.Length >= 2 ? parts[1].TrimEnd('/') : "/";

        return new HealthHttpRequestLine(method, path);
    }

    /// <summary>Собирает HTTP-ответ.</summary>
    private static string BuildHttpResponse(int statusCode, string contentType, string body)
    {
        var reason = statusCode switch
        {
            200 => "OK",
            503 => "Service Unavailable",
            404 => "Not Found",
            405 => "Method Not Allowed",
            _ => "Unknown",
        };

        return $"HTTP/1.1 {statusCode} {reason}\r\n" +
               $"Content-Type: {contentType}\r\n" +
               $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
               $"Cache-Control: no-cache, no-store, must-revalidate\r\n" +
               $"Connection: close\r\n" +
               $"\r\n" +
               $"{body}";
    }

    /// <summary>Маршрутизирует запрос к соответствующему обработчику.</summary>
    private async Task<(int statusCode, string contentType, string body)> ServeAsync(
        HealthHttpRequestLine requestLine,
        CancellationToken ct)
    {
        try
        {
            if (!string.Equals(requestLine.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                return (405, "text/plain", "Method Not Allowed");
            }

            var path = requestLine.Path;

            return path == _liveEndpoint || path == _liveEndpoint + "/"
                ? ServeLiveness()
                : path == _readyEndpoint || path == _readyEndpoint + "/"
                ? await ServeReadinessAsync(ct)
                : path == _healthEndpoint || path == _healthEndpoint + "/" || path == "" || path == "/"
                ? await ServeDetailedAsync(ct)
                : ((int statusCode, string contentType, string body))(404, "text/plain", "Not Found");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Health check error for path {Path}", requestLine.Path);
            return (503, "application/json",
                $$"""{"status":"error","message":{{JsonSerializer.Serialize(ex.Message)}}}""");
        }
    }

    /// <summary>
    /// Liveness probe: процесс жив, если этот код выполняется.
    /// Всегда возвращает 200 OK.
    /// </summary>
    private static (int statusCode, string contentType, string body) ServeLiveness()
    {
        return (200, "application/json", """{"status":"alive"}""");
    }

    /// <summary>
    /// Readiness probe: проверяет доступность БД.
    /// Возвращает 200 OK если БД доступна, 503 Service Unavailable если нет.
    /// </summary>
    private async Task<(int statusCode, string contentType, string body)> ServeReadinessAsync(CancellationToken ct)
    {
        var dbOk = await CheckDatabaseAsync(ct);

        if (dbOk)
        {
            logger.LogDebug("Health /ready: OK [db=ok]");
            return (200, "application/json", """{"status":"ready","database":"healthy"}""");
        }

        logger.LogWarning("Health /ready: NOT READY [db=unavailable]");
        return (503, "application/json", """{"status":"not ready","database":"unhealthy"}""");
    }

    /// <summary>
    /// Детальный health check: JSON-отчёт со всеми проверками и метаданными.
    /// Результаты кэшируются на CacheSeconds секунд для снижения нагрузки.
    /// </summary>
    private async Task<(int statusCode, string contentType, string body)> ServeDetailedAsync(CancellationToken ct)
    {
        // Используем кэш, если он ещё валиден
        if (_cachedResult != null && (DateTime.UtcNow - _lastCheckTime).TotalSeconds < _options.CacheSeconds)
        {
            var cachedJson = JsonSerializer.Serialize(_cachedResult, HealthJsonContext.Default.HealthCheckResult);
            return (_cachedResult.Status == "healthy" ? 200 : 503, "application/json", cachedJson);
        }

        var dbHealthy = await CheckDatabaseAsync(ct);
        var checks = new List<HealthComponent>
        {
            new() { Name = "database", Status = dbHealthy ? "healthy" : "unhealthy" },
            new() { Name = "process", Status = "healthy" },
        };

        // Дополнительные проверки (BIM, Telegram API и т.д.)
        foreach (var (name, checkFunc) in AdditionalChecks)
        {
            try
            {
                var componentStatus = await checkFunc(ct);
                checks.Add(new HealthComponent
                {
                    Name = name,
                    Status = componentStatus.Status,
                    Message = componentStatus.Message,
                });
            }
            catch (Exception ex)
            {
                checks.Add(new HealthComponent
                {
                    Name = name,
                    Status = "error",
                    Message = ex.Message,
                });
            }
        }

        var allHealthy = checks.TrueForAll(c => c.Status == "healthy");
        var uptime = GetUptime();

        var checksLine = string.Join(", ", checks.Select(c =>
            c.Status == "healthy" ? $"{c.Name}=ok" : $"{c.Name}=FAIL({c.Status})"));

        if (allHealthy)
        {
            logger.LogDebug("Health /health: OK [{Checks}] uptime={Uptime}", checksLine, uptime);
        }
        else
        {
            logger.LogWarning("Health /health: UNHEALTHY [{Checks}] uptime={Uptime}", checksLine, uptime);
        }

        var result = new HealthCheckResult
        {
            Status = allHealthy ? "healthy" : "unhealthy",
            Service = _options.ServiceName,
            Timestamp = DateTime.UtcNow.ToString("O"),
            Uptime = uptime,
            Version = GetVersion(),
            Checks = checks,
        };

        // Обновляем кэш
        _cachedResult = result;
        _lastCheckTime = DateTime.UtcNow;

        var json = JsonSerializer.Serialize(result, HealthJsonContext.Default.HealthCheckResult);
        return (allHealthy ? 200 : 503, "application/json", json);
    }

    /// <summary>Проверяет доступность БД через установленную функцию проверки.</summary>
    private async Task<bool> CheckDatabaseAsync(CancellationToken ct)
    {
        if (DatabaseCheckAsync == null)
        {
            return true; // Нет функции проверки — считаем, что БД доступна
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.DbCheckTimeoutSeconds));

            return await DatabaseCheckAsync(cts.Token);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Возвращает uptime процесса.</summary>
    private static string GetUptime()
    {
        var uptime = DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime();
        return uptime.TotalDays >= 1
            ? $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m"
            : $"{(int)uptime.TotalHours}h {uptime.Minutes}m {uptime.Seconds}s";
    }

    /// <summary>Возвращает версию сборки.</summary>
    private static string GetVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(HealthCheckHostedService).Assembly;
        var version = assembly.GetName().Version;
        return version?.ToString() ?? "unknown";
    }
}

internal readonly record struct HealthHttpRequestLine(string Method, string Path);

/// <summary>Детальный результат health check для /health.</summary>
public sealed class HealthCheckResult
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "healthy";

    [JsonPropertyName("service")]
    public string Service { get; set; } = "unknown";

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = "";

    [JsonPropertyName("uptime")]
    public string Uptime { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("checks")]
    public List<HealthComponent> Checks { get; set; } = [];
}

/// <summary>Компонент проверки в детальном отчёте.</summary>
public sealed class HealthComponent
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }
}

/// <summary>Статус компонента для дополнительных проверок.</summary>
public sealed class HealthComponentStatus
{
    public string Status { get; set; } = "healthy";
    public string? Message { get; set; }
}

/// <summary>Source-generated JSON serializer context для AOT-совместимости.</summary>
[JsonSerializable(typeof(HealthCheckResult))]
[JsonSerializable(typeof(HealthComponent))]
internal sealed partial class HealthJsonContext : JsonSerializerContext;
