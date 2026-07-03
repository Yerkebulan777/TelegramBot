using System.Diagnostics;
using System.Text;
using TelegramBot.Core.Helpers;
using TelegramBot.Core.Models;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Потоковая обработка stdout/stderr процесса с ограничением размера.
/// </summary>
public sealed class OutputCollector(ILogger<OutputCollector> logger)
{
    private const int MaxOutputChars = 64 * 1024; // 64KB лимит

    /// <summary>
    /// Настраивает обработчики stdout/stderr для процесса.
    /// </summary>
    public (StringBuilder Output, StringBuilder Error, IDisposable Subscription) SetupProcessOutput(Process process)
    {
        var outputBuilder = new StringBuilder(capacity: 1024);
        var errorBuilder = new StringBuilder(capacity: 1024);
        var outputTruncated = false;
        var errorTruncated = false;

        void OnOutputDataReceived(object? sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                outputBuilder.AppendBounded(e.Data, ref outputTruncated, MaxOutputChars);
            }
        }

        void OnErrorDataReceived(object? sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                errorBuilder.AppendBounded(e.Data, ref errorTruncated, MaxOutputChars);
            }
        }

        process.OutputDataReceived += OnOutputDataReceived;
        process.ErrorDataReceived += OnErrorDataReceived;

        return (outputBuilder, errorBuilder, new OutputSubscription(process, OnOutputDataReceived, OnErrorDataReceived));
    }

    /// <summary>
    /// Логирует вывод процесса с информацией о truncation.
    /// </summary>
    public void LogOutput(PendingCommand cmd, StringBuilder outputBuilder, StringBuilder errorBuilder, bool outputTruncated, bool errorTruncated)
    {
        if (outputBuilder.Length > 0)
        {
            var outputInfo = outputTruncated
                ? $"{TruncateOutput(outputBuilder)} [TRUNCATED: 64KB limit reached]"
                : TruncateOutput(outputBuilder);

            logger.LogDebug("Output [{Cmd} {Id} {CorrelationId}, truncated={Truncated}]: {Output}",
                cmd.CommandText, cmd.CommandId, cmd.CorrelationId, outputTruncated, outputInfo);
        }

        if (errorBuilder.Length > 0)
        {
            var errorInfo = errorTruncated
                ? $"{TruncateOutput(errorBuilder)} [TRUNCATED: 64KB limit reached]"
                : TruncateOutput(errorBuilder);

            logger.LogWarning("Stderr [{Cmd} {Id} {CorrelationId}, truncated={Truncated}]: {Error}",
                cmd.CommandText, cmd.CommandId, cmd.CorrelationId, errorTruncated, errorInfo);
        }
    }

    private static string TruncateOutput(StringBuilder builder)
    {
        const int maxLength = 4096;
        return builder.Length > maxLength
            ? builder.ToString(0, maxLength) + $"\n... (truncated for log, total {builder.Length} chars)"
            : builder.ToString(0, builder.Length);
    }

    private sealed class OutputSubscription : IDisposable
    {
        private readonly Process _process;
        private readonly DataReceivedEventHandler _outputHandler;
        private readonly DataReceivedEventHandler _errorHandler;
        private bool _disposed;

        public OutputSubscription(Process process, DataReceivedEventHandler outputHandler, DataReceivedEventHandler errorHandler)
        {
            _process = process;
            _outputHandler = outputHandler;
            _errorHandler = errorHandler;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _process.OutputDataReceived -= _outputHandler;
                _process.ErrorDataReceived -= _errorHandler;
                _disposed = true;
            }
        }
    }
}
