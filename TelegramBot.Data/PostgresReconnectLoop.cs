using Microsoft.Extensions.Logging;

namespace TelegramBot.Data;

/// <summary>
/// Helper for reconnecting PostgreSQL LISTEN/NOTIFY listener loops.
/// Provides the standard outer retry pattern: catch exception → log → delay → retry.
/// </summary>
public static class PostgresReconnectLoop
{
    /// <summary>Default delay between reconnection attempts (5000 ms).</summary>
    public const int DefaultReconnectDelayMs = 5_000;

    /// <summary>
    /// Runs the inner listener loop function with automatic reconnection on failure.
    /// On any exception (except <see cref="OperationCanceledException"/>), waits
    /// <paramref name="reconnectDelayMs"/> before retrying.
    /// </summary>
    /// <param name="serviceName">Human-readable service name for logging.</param>
    /// <param name="runInnerLoopAsync">The inner listener loop to execute.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="reconnectDelayMs">Delay between reconnection attempts (default 5000ms).</param>
    /// <param name="stoppingToken">Cancellation token to stop the loop.</param>
    public static async Task RunAsync(
        string serviceName,
        Func<CancellationToken, Task> runInnerLoopAsync,
        ILogger logger,
        int reconnectDelayMs = DefaultReconnectDelayMs,
        CancellationToken stoppingToken = default)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await runInnerLoopAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Service} listener lost: retryMs={Delay}", serviceName, reconnectDelayMs);
                await Task.Delay(reconnectDelayMs, stoppingToken);
            }
        }
    }
}
