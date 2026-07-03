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
    public ProcessOutputCapture SetupProcessOutput(Process process)
    {
        return new ProcessOutputCapture(process);
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

    public sealed class ProcessOutputCapture : IDisposable
    {
        private readonly Process _process;
        private bool _outputTruncated;
        private bool _errorTruncated;
        private bool _disposed;

        public ProcessOutputCapture(Process process)
        {
            _process = process;
            process.OutputDataReceived += OnOutputDataReceived;
            process.ErrorDataReceived += OnErrorDataReceived;
        }

        public StringBuilder Output { get; } = new(capacity: 1024);
        public StringBuilder Error { get; } = new(capacity: 1024);
        public bool OutputTruncated => _outputTruncated;
        public bool ErrorTruncated => _errorTruncated;

        private void OnOutputDataReceived(object? sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                Output.AppendBounded(e.Data, ref _outputTruncated, MaxOutputChars);
            }
        }

        private void OnErrorDataReceived(object? sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                Error.AppendBounded(e.Data, ref _errorTruncated, MaxOutputChars);
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _process.OutputDataReceived -= OnOutputDataReceived;
                _process.ErrorDataReceived -= OnErrorDataReceived;
                _disposed = true;
            }
        }
    }
}
