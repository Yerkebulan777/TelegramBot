using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TelegramBot.Core.Config;

namespace TelegramBot.Core.Health;

/// <summary>
/// Background service, запускающий минимальный HTTP-сервер readiness probe на основе <see cref="TcpListener"/>.
/// Единственный эндпоинт <c>GET /health/ready</c> — проверяет доступность БД.
/// </summary>
public sealed class HealthCheckHostedService(
    IOptions<HealthCheckOptions> options,
    ILogger<HealthCheckHostedService> logger)
    : BackgroundService
{
    private readonly HealthCheckOptions _options = options.Value;

    /// <summary>
    /// Функция, выполняющая проверку БД для readiness probe.
    /// Устанавливается при регистрации сервиса в DI.
    /// </summary>
    public Func<CancellationToken, Task<bool>>? DatabaseCheckAsync { get; set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, _options.Port);

        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Health check server failed to start on port {Port}", _options.Port);
            return;
        }

        logger.LogInformation("Health check ready: http://localhost:{Port}/health/ready  ({Service})",
            _options.Port, _options.ServiceName);

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
                var (statusCode, body) = await ServeReadyAsync(ct);
                var response = BuildHttpResponse(statusCode, body);

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

    private static string BuildHttpResponse(int statusCode, string body)
    {
        var reason = statusCode switch
        {
            200 => "OK",
            503 => "Service Unavailable",
            _ => "Unknown",
        };

        return $"HTTP/1.1 {statusCode} {reason}\r\n" +
               $"Content-Type: application/json\r\n" +
               $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
               $"Cache-Control: no-cache, no-store, must-revalidate\r\n" +
               $"Connection: close\r\n" +
               $"\r\n" +
               $"{body}";
    }

    private async Task<(int statusCode, string body)> ServeReadyAsync(CancellationToken ct)
    {
        var dbOk = await CheckDatabaseAsync(ct);

        if (dbOk)
        {
            logger.LogDebug("Health /ready: OK [db=ok]");
            return (200, """{"status":"ready","database":"healthy"}""");
        }

        logger.LogWarning("Health /ready: NOT READY [db=unavailable]");
        return (503, """{"status":"not ready","database":"unhealthy"}""");
    }

    private async Task<bool> CheckDatabaseAsync(CancellationToken ct)
    {
        if (DatabaseCheckAsync == null)
        {
            return true;
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
}
