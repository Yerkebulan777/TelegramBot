using Microsoft.Extensions.Logging;

namespace TelegramBot.Data;

/// <summary>
/// Helper for reconnecting PostgreSQL LISTEN/NOTIFY listener loops.
/// Provides the standard outer retry pattern with exponential backoff:
/// catch exception → log → delay (with backoff) → retry.
/// </summary>
public static class PostgresReconnectLoop
{
    /// <summary>Default delay between reconnection attempts (5000 ms).</summary>
    public const int DefaultReconnectDelayMs = 5_000;

    /// <summary>Maximum delay between reconnection attempts (60000 ms).</summary>
    public const int MaxReconnectDelayMs = 60_000;

    /// <summary>Exponential backoff multiplier.</summary>
    private const double BackoffMultiplier = 1.5;

    /// <summary>
    /// Runs the inner listener loop function with automatic reconnection on failure.
    /// On any exception (except <see cref="OperationCanceledException"/>), waits
    /// with exponential backoff before retrying (starting from <paramref name="initialDelayMs"/>,
    /// capped at <see cref="MaxReconnectDelayMs"/>).
    /// </summary>
    /// <param name="serviceName">Human-readable service name for logging.</param>
    /// <param name="runInnerLoopAsync">The inner listener loop to execute.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="initialDelayMs">Initial delay between reconnection attempts (default 5000ms).</param>
    /// <param name="stoppingToken">Cancellation token to stop the loop.</param>
    public static async Task RunAsync(
        string serviceName,
        Func<CancellationToken, Task> runInnerLoopAsync,
        ILogger logger,
        int initialDelayMs = DefaultReconnectDelayMs,
        CancellationToken stoppingToken = default)
    {
        var currentDelayMs = initialDelayMs;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await runInnerLoopAsync(stoppingToken);
                // Reset delay on success
                currentDelayMs = initialDelayMs;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Service} listener lost: retryMs={Delay}", serviceName, currentDelayMs);

                try
                {
                    await Task.Delay(currentDelayMs, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // Apply exponential backoff, capped at MaxReconnectDelayMs
                currentDelayMs = Math.Min((int)(currentDelayMs * BackoffMultiplier), MaxReconnectDelayMs);
            }
        }
    }
}
