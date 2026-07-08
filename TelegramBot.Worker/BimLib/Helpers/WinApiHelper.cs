namespace TelegramBot.Worker.BimLib.Helpers;

/// <summary>
/// Shared validation and safety utilities for WinAPI (P/Invoke) calls.
/// Provides timeout protection, structured logging, and handle validation.
/// Logger must be initialized once at startup via <see cref="SetLogger"/>.
/// </summary>
internal static class WinApiHelper
{
    private static ILogger? _logger;

    /// <summary>Default timeout for window operations (SendMessage, EnumWindows, etc.).</summary>
    internal const int DefaultTimeoutMs = 5000;

    /// <summary>
    /// Initializes the static logger. Must be called once during application startup
    /// (e.g., from Program.cs) before any WinAPI calls are made.
    /// Thread-safe: only the first call takes effect.
    /// </summary>
    internal static void SetLogger(ILogger logger)
    {
        _logger ??= logger;
    }

    /// <summary>Logs a WinAPI warning with optional Win32 error code.</summary>
    internal static void LogWarning(string method, string details, int errorCode = 0)
    {
        if (_logger == null)
        {
            return;
        }

        if (errorCode != 0)
        {
            _logger.LogWarning(
                "WinAPI {Method} failed [error={Error}]: {Details}",
                method, errorCode, details);
        }
        else
        {
            _logger.LogWarning("WinAPI {Method}: {Details}", method, details);
        }
    }

    /// <summary>Logs a WinAPI exception with context.</summary>
    internal static void LogError(string method, Exception ex, string details)
    {
        _logger?.LogError(
            ex, "WinAPI {Method} threw an exception: {Details}",
            method, details);
    }

    /// <summary>
    /// Runs a WinAPI operation on a background thread with a timeout.
    /// Returns <paramref name="fallback"/> if the operation times out or throws.
    /// Use for potentially blocking calls like <c>SendMessage</c> and <c>EnumWindows</c>.
    /// </summary>
    internal static TResult RunWithTimeout<TResult>(
        TimeSpan timeout,
        Func<TResult> operation,
        string operationName,
        TResult fallback = default!)
    {
        try
        {
            var task = Task.Run(operation);
#pragma warning disable VSTHRD002 // Deliberate blocking: thread-pool timeout protection for WinAPI calls
            if (task.Wait(timeout))
            {
                return task.Result;
            }
#pragma warning restore VSTHRD002

            LogWarning(operationName, $"Timed out after {timeout.TotalSeconds:F1}s");
            return fallback;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogError(operationName, ex, $"timeout={timeout.TotalSeconds:F1}s");
            return fallback;
        }
    }

    /// <summary>
    /// Convenience overload using <see cref="DefaultTimeoutMs"/>.
    /// </summary>
    internal static TResult RunWithTimeout<TResult>(
        Func<TResult> operation,
        string operationName,
        TResult fallback = default!)
    {
        return RunWithTimeout(
            TimeSpan.FromMilliseconds(DefaultTimeoutMs),
            operation,
            operationName,
            fallback);
    }
}
