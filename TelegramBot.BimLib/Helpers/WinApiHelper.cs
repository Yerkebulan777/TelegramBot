using Microsoft.Extensions.Logging;

namespace TelegramBot.BimLib.Helpers;

/// <summary>
/// Shared structured-logging utilities for WinAPI (P/Invoke) calls.
/// Logger must be initialized once at startup via <see cref="SetLogger"/>.
/// </summary>
public static class WinApiHelper
{
    private static ILogger? _logger;

    /// <summary>
    /// Initializes the static logger. Must be called once during application startup
    /// (e.g., from Program.cs) before any WinAPI calls are made.
    /// Thread-safe: only the first call takes effect.
    /// </summary>
    public static void SetLogger(ILogger logger)
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
                "WinAPI {Method} err={Error}: {Details}",
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
            ex, "WinAPI {Method} exception: {Details}",
            method, details);
    }
}
