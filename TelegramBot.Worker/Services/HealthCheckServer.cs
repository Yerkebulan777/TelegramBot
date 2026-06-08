using System.Net;
using System.Net.Sockets;
using System.Text;
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
    private readonly int _port;
    private readonly string _connectionString;
    private readonly ILogger<HealthCheckServer> _logger;
    private TcpListener? _listener;

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
                // Fire-and-forget: каждый запрос обрабатывается параллельно
                _ = HandleRequestAsync(client, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _listener.Stop();
        }
    }

    private async Task HandleRequestAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            await using var stream = client.GetStream();

            // Читаем только первую строку HTTP-запроса (достаточно для path-based routing)
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(ct);
            if (requestLine == null) return;

            var path = requestLine.Split(' ').ElementAtOrDefault(1) ?? "/";

            var (statusCode, statusText, body) = path switch
            {
                "/healthz" => (200, "OK", "Healthy"),
                "/readyz" => await CheckReadinessAsync(ct),
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
        }
    }

    private async Task<(int code, string text, string body)> CheckReadinessAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await NpgsqlHelper.CreateOpenConnectionAsync(_connectionString, ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteNonQueryAsync(ct);
            return (200, "OK", "Ready");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Readiness check failed: database unreachable");
            return (503, "Service Unavailable", "Database unreachable");
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
