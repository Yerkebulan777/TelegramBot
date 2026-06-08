using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using TelegramBot.Data;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Простой HTTP health check сервер для liveness (/healthz) и readiness (/readyz) probes.
/// Использует TcpListener без ASP.NET зависимостей.
/// Порт настраивается через HealthCheck:Port (по умолчанию 5001).
/// Привязан к localhost — не доступен снаружи без reverse proxy.
/// </summary>
internal sealed class HealthCheckServer : BackgroundService
{
    private const int ReadinessCacheSeconds = 5;
    private const int ReadTimeoutMs = 5_000;
    private const int DbCheckTimeoutSeconds = 3;

    private readonly int _port;
    private readonly string _connectionString;
    private readonly ILogger<HealthCheckServer> _logger;
    private TcpListener? _listener;

    // Кеш readiness check — не долбим БД на каждый /readyz
    private readonly object _cacheLock = new();
    private (int code, string text, string body) _cachedReadiness = (503, "Service Unavailable", "Starting");
    private DateTime _nextReadinessCheck = DateTime.MinValue;

    // Счётчик активных запросов для graceful shutdown
    private int _activeRequests;

    public HealthCheckServer(IConfiguration configuration, ILogger<HealthCheckServer> logger)
    {
        _port = configuration.GetValue<int>("HealthCheck:Port", 5001);
        _connectionString = configuration.GetConnectionString("Postgres")
            ?? "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres";
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        _logger.LogInformation("Health check listening on 127.0.0.1:{Port}", _port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                Interlocked.Increment(ref _activeRequests);
                // Каждый запрос обрабатывается параллельно
                _ = HandleRequestAsync(client, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _listener.Stop();

            // Ждём завершения активных запросов (макс 10 сек)
            _logger.LogInformation("Health check: waiting for {Count} active requests",
                Volatile.Read(ref _activeRequests));

            for (var i = 0; i < 20 && Volatile.Read(ref _activeRequests) > 0; i++)
            {
                await Task.Delay(500, CancellationToken.None);
            }

            if (Volatile.Read(ref _activeRequests) > 0)
            {
                _logger.LogWarning("Health check: {Count} requests still active after shutdown",
                    Volatile.Read(ref _activeRequests));
            }
        }
    }

    private async Task HandleRequestAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            // Таймаут на чтение запроса — защита от медленных/битых клиентов
            client.ReceiveTimeout = ReadTimeoutMs;

            await using var stream = client.GetStream();

            // Читаем только первую строку HTTP-запроса (достаточно для path-based routing)
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(ct);
            if (requestLine == null) return;

            var path = requestLine.Split(' ').ElementAtOrDefault(1) ?? "/";

            var (statusCode, statusText, body) = path switch
            {
                "/healthz" => (200, "OK", "Healthy"),
                "/readyz" => await GetReadinessCachedAsync(ct),
                _ => (404, "Not Found", "Not Found")
            };

            var response = Encoding.UTF8.GetBytes(
                $"HTTP/1.1 {statusCode} {statusText}\r\n" +
                $"Content-Type: text/plain\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                $"Connection: close\r\n" +
                $"\r\n" +
                $"{body}");

            await stream.WriteAsync(response, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Health check request failed");
        }
        finally
        {
            client.Dispose();
            Interlocked.Decrement(ref _activeRequests);
        }
    }

    /// <summary>
    /// Возвращает кешированный результат readiness check.
    /// Реальный запрос к БД делается не чаще 1 раза в 5 секунд.
    /// </summary>
    private async Task<(int code, string text, string body)> GetReadinessCachedAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        lock (_cacheLock)
        {
            if (now < _nextReadinessCheck)
            {
                return _cachedReadiness;
            }
            _nextReadinessCheck = now.AddSeconds(ReadinessCacheSeconds);
        }

        // Реальный check с таймаутом 3 секунды
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(DbCheckTimeoutSeconds));

            await using var conn = await NpgsqlHelper.CreateOpenConnectionAsync(_connectionString, timeoutCts.Token);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteNonQueryAsync(timeoutCts.Token);

            var result = (200, "OK", "Ready");
            lock (_cacheLock) { _cachedReadiness = result; }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Readiness check failed: database unreachable");
            var result = (503, "Service Unavailable", "Database unreachable");
            lock (_cacheLock) { _cachedReadiness = result; }
            return result;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _listener?.Stop();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _listener?.Dispose();
        base.Dispose();
    }
}
