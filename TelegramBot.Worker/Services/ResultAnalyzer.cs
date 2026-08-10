using System.Diagnostics;
using System.Xml.Serialization;
using TelegramBot.Core.Helpers;
using TelegramBot.Core.Models;
using TelegramBot.Worker.Helpers;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Анализирует результат выполнения команды: ResultFile от плагина или exit code.
/// </summary>
public sealed class ResultAnalyzer(CommandPreparer commandPreparer, ILogger<ResultAnalyzer> logger)
{
    private static readonly XmlSerializer ResultFileSerializer = new(typeof(ResultFile));
    private const int ResultFileReadRetryCount = 50;
    private static readonly TimeSpan ResultFileReadRetryDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Пробует прочитать result-файл и возвращает статус.
    /// </summary>
    public async Task<(ResultFileReadStatus Status, ResultFile? Result, string? ErrorMessage)> TryReadResultFileAsync(
        int commandId,
        string filePath,
        CancellationToken ct)
    {
        var (path, _) = commandPreparer.GetTaskFilePaths(commandId, filePath);

        if (!File.Exists(path))
        {
            return (ResultFileReadStatus.NotFound, null, null);
        }

        for (var attempt = 0; attempt <= ResultFileReadRetryCount; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var result = (ResultFile)ResultFileSerializer.Deserialize(stream)!;

                if (result.Status is ResultStatus.Done or ResultStatus.Failed or ResultStatus.Cancelled)
                {
                    DeleteResultFile(path);
                    return (ResultFileReadStatus.Valid, result, null);
                }

                RenameToBadFile(path);
                return (ResultFileReadStatus.Invalid, null, $"Plugin result file has invalid status: {path}");
            }
            catch (InvalidOperationException ex)
            {
                if (await RetryReadAsync(attempt, ct))
                {
                    continue;
                }

                RenameToBadFile(path);
                return (ResultFileReadStatus.Invalid, null, $"Plugin result file contains invalid XML: {path}. {ex.Message}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (await RetryReadAsync(attempt, ct))
                {
                    continue;
                }

                return (ResultFileReadStatus.Invalid, null, $"Plugin result file cannot be read: {path}. {ex.Message}");
            }
        }

        return (ResultFileReadStatus.Invalid, null, $"Plugin result file cannot be read: {path}");
    }

    /// <summary>
    /// Определяет финальный статус команды на основе result-файла или exit code.
    /// </summary>
    public CommandResult DetermineResult(
        PendingCommand cmd,
        ResultFileReadStatus resultReadStatus,
        ResultFile? result,
        string? resultReadError,
        Process process,
        Stopwatch sw)
    {
        if (resultReadStatus == ResultFileReadStatus.Valid && result != null)
        {
            return AnalyzePluginResult(cmd, result, sw);
        }

        if (resultReadStatus == ResultFileReadStatus.Invalid)
        {
            return CommandResult.Failure(resultReadError ?? "Invalid plugin result file", null);
        }

        // Fallback по exit code
        if (process.ExitCode == 0 && !CommandPreparer.IsRevitCommand(cmd.CommandText))
        {
            logger.LogWarning(
                "Exit=0 no result file: id={Id}, corr={CorrelationId}, cmd={Cmd}, ms={ElapsedMs}",
                cmd.CommandId, cmd.CorrelationId, cmd.CommandText, sw.ElapsedMilliseconds);

            return CommandResult.Success();
        }

        var errorMessage = process.ExitCode == 0
            ? "Revit exited without writing the required ResultFile"
            : $"Process exited with code {ExitCodeFormatter.Format(process.ExitCode)}";

        if (process.ExitCode < 0)
        {
            var processStartUtc = DateTime.UtcNow - sw.Elapsed;
            var journalEvidence = RevitJournalHelper.TryGetCrashEvidence(process.StartInfo.FileName, processStartUtc);
            if (journalEvidence != null)
            {
                logger.LogWarning("Journal evidence: id={Id}, corr={CorrelationId}\n{Evidence}",
                    cmd.CommandId, cmd.CorrelationId, journalEvidence);
            }
        }

        return CommandResult.Failure(errorMessage, process.ExitCode);
    }

    private CommandResult AnalyzePluginResult(PendingCommand cmd, ResultFile result, Stopwatch sw)
    {
        if (result.Status == ResultStatus.Done)
        {
            logger.LogInformation(
                "Plugin done: id={Id}, corr={CorrelationId}, cmd={Cmd}, status={Status}, out={OutputPath}, pluginMs={PluginMs}, ms={ElapsedMs}",
                cmd.CommandId, cmd.CorrelationId, cmd.CommandText, result.Status, result.OutputFiles ?? "<none>",
                result.ExecutionTimeMilliseconds, sw.ElapsedMilliseconds);

            var warningMessage = string.IsNullOrWhiteSpace(result.WarningMessage)
                ? null
                : result.WarningMessage;

            if (warningMessage is not null)
            {
                logger.LogWarning(
                    "Plugin warning: id={Id}, corr={CorrelationId}, warn={Warning}",
                    cmd.CommandId, cmd.CorrelationId, warningMessage);
            }

            return CommandResult.Success(warningMessage);
        }

        if (result.Status == ResultStatus.Cancelled)
        {
            var cancellationMessage = result.ErrorMessage ?? "Plugin reported cancellation";
            logger.LogInformation(
                "Plugin cancelled: id={Id}, corr={CorrelationId}, cmd={Cmd}, pluginMs={PluginMs}, ms={ElapsedMs}, err={Error}",
                cmd.CommandId, cmd.CorrelationId, cmd.CommandText, result.ExecutionTimeMilliseconds,
                sw.ElapsedMilliseconds, cancellationMessage);

            return CommandResult.Cancelled(cancellationMessage);
        }

        // Failed
        logger.LogWarning(
            "Plugin fail: id={Id}, corr={CorrelationId}, cmd={Cmd}, status={Status}, pluginMs={PluginMs}, ms={ElapsedMs}, err={Error}",
            cmd.CommandId, cmd.CorrelationId, cmd.CommandText, result.Status, result.ExecutionTimeMilliseconds,
            sw.ElapsedMilliseconds, result.ErrorMessage ?? "Plugin reported failure");

        if (!string.IsNullOrWhiteSpace(result.ErrorDetails))
        {
            logger.LogDebug("Plugin errDetails: id={Id}: {Details}", cmd.CommandId, result.ErrorDetails);
        }

        return CommandResult.Failure(result.ErrorMessage ?? "Plugin reported failure", null, isPluginOrigin: true);
    }

    private void RenameToBadFile(string path)
    {
        try
        {
            File.Move(path, path + ".bad", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Rename to .bad fail: {Path}", path);
        }
    }

    private void DeleteResultFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Delete result file fail: {Path}", path);
        }
    }

    private static async Task<bool> RetryReadAsync(int attempt, CancellationToken ct)
    {
        if (attempt >= ResultFileReadRetryCount)
        {
            return false;
        }

        await Task.Delay(ResultFileReadRetryDelay, ct);
        return true;
    }

    public enum ResultFileReadStatus
    {
        NotFound,
        Valid,
        Invalid,
    }

    public sealed class CommandResult
    {
        public bool IsSuccess { get; }
        public bool IsFailure { get; }
        public bool IsCancelled { get; }
        public bool IsPluginOrigin { get; private set; }
        public string? ErrorMessage { get; }
        public string? WarningMessage { get; }
        public int? ExitCode { get; }

        private CommandResult(
            bool isSuccess,
            bool isFailure,
            bool isCancelled,
            string? errorMessage,
            int? exitCode,
            bool isPluginOrigin,
            string? warningMessage = null)
        {
            IsSuccess = isSuccess;
            IsFailure = isFailure;
            IsCancelled = isCancelled;
            ErrorMessage = errorMessage;
            ExitCode = exitCode;
            IsPluginOrigin = isPluginOrigin;
            WarningMessage = warningMessage;
        }

        public static CommandResult Success(string? warningMessage = null)
        {
            // Whitespace-only warnings are treated as absent — never persist them as ErrorMessage.
            warningMessage = string.IsNullOrWhiteSpace(warningMessage) ? null : warningMessage;
            return new(true, false, false, null, null, false, warningMessage);
        }

        public static CommandResult Failure(string errorMessage, int? exitCode, bool isPluginOrigin = false)
        {
            return new(false, true, false, errorMessage, exitCode, isPluginOrigin);
        }

        public static CommandResult Cancelled(string errorMessage)
        {
            return new(false, false, true, errorMessage, null, false);
        }
    }
}
