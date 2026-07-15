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
    public ResultFileReadStatus TryReadResultFile(int commandId, string filePath, out ResultFile result, out string? errorMessage)
    {
        var (path, _) = commandPreparer.GetTaskFilePaths(commandId, filePath);

        if (!File.Exists(path))
        {
            result = null!;
            errorMessage = null;
            return ResultFileReadStatus.NotFound;
        }

        for (var attempt = 0; attempt <= ResultFileReadRetryCount; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                result = (ResultFile)ResultFileSerializer.Deserialize(stream)!;

                if (result.Status is ResultStatus.Done or ResultStatus.Failed or ResultStatus.Cancelled)
                {
                    DeleteResultFile(path);
                    errorMessage = null;
                    return ResultFileReadStatus.Valid;
                }

                RenameToBadFile(path);
                result = null!;
                errorMessage = $"Plugin result file has invalid status: {path}";
                return ResultFileReadStatus.Invalid;
            }
            catch (InvalidOperationException ex)
            {
                if (RetryRead(attempt))
                {
                    continue;
                }

                RenameToBadFile(path);
                result = null!;
                errorMessage = $"Plugin result file contains invalid XML: {path}. {ex.Message}";
                return ResultFileReadStatus.Invalid;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (RetryRead(attempt))
                {
                    continue;
                }

                result = null!;
                errorMessage = $"Plugin result file cannot be read: {path}. {ex.Message}";
                return ResultFileReadStatus.Invalid;
            }
        }

        result = null!;
        errorMessage = $"Plugin result file cannot be read: {path}";
        return ResultFileReadStatus.Invalid;
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
            return CommandResult.Failure(resultReadError ?? "Invalid plugin result file", null, sw.ElapsedMilliseconds);
        }

        // Fallback по exit code
        if (process.ExitCode == 0 && !CommandPreparer.IsRevitCommand(cmd.CommandText))
        {
            logger.LogWarning(
                "Exit=0 no result file: id={Id}, corr={CorrelationId}, cmd={Cmd}, ms={ElapsedMs}",
                cmd.CommandId, cmd.CorrelationId, cmd.CommandText, sw.ElapsedMilliseconds);

            return CommandResult.Success(sw.ElapsedMilliseconds);
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

        return CommandResult.Failure(errorMessage, process.ExitCode, sw.ElapsedMilliseconds);
    }

    private CommandResult AnalyzePluginResult(PendingCommand cmd, ResultFile result, Stopwatch sw)
    {
        if (result.Status == ResultStatus.Done)
        {
            logger.LogInformation(
                "Plugin done: id={Id}, corr={CorrelationId}, cmd={Cmd}, status={Status}, out={OutputPath}, ms={ElapsedMs}",
                cmd.CommandId, cmd.CorrelationId, cmd.CommandText, result.Status, result.OutputFiles ?? "<none>", sw.ElapsedMilliseconds);

            return CommandResult.Success(sw.ElapsedMilliseconds);
        }

        if (result.Status == ResultStatus.Cancelled)
        {
            var cancellationMessage = result.ErrorMessage ?? "Plugin reported cancellation";
            logger.LogInformation(
                "Plugin cancelled: id={Id}, corr={CorrelationId}, cmd={Cmd}, ms={ElapsedMs}, err={Error}",
                cmd.CommandId, cmd.CorrelationId, cmd.CommandText, sw.ElapsedMilliseconds, cancellationMessage);

            return CommandResult.Cancelled(cancellationMessage, sw.ElapsedMilliseconds);
        }

        // Failed
        logger.LogWarning(
            "Plugin fail: id={Id}, corr={CorrelationId}, cmd={Cmd}, status={Status}, ms={ElapsedMs}, err={Error}",
            cmd.CommandId, cmd.CorrelationId, cmd.CommandText, result.Status, sw.ElapsedMilliseconds, result.ErrorMessage ?? "Plugin reported failure");

        if (!string.IsNullOrWhiteSpace(result.ErrorDetails))
        {
            logger.LogDebug("Plugin errDetails: id={Id}: {Details}", cmd.CommandId, result.ErrorDetails);
        }

        return CommandResult.Failure(result.ErrorMessage ?? "Plugin reported failure", null, sw.ElapsedMilliseconds, isPluginOrigin: true);
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

    private static bool RetryRead(int attempt)
    {
        if (attempt >= ResultFileReadRetryCount)
        {
            return false;
        }

        Thread.Sleep(ResultFileReadRetryDelay);
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
        public int? ExitCode { get; }
        public long ElapsedMs { get; }

        private CommandResult(bool isSuccess, bool isFailure, bool isCancelled, string? errorMessage, int? exitCode, long elapsedMs, bool isPluginOrigin)
        {
            IsSuccess = isSuccess;
            IsFailure = isFailure;
            IsCancelled = isCancelled;
            ErrorMessage = errorMessage;
            ExitCode = exitCode;
            ElapsedMs = elapsedMs;
            IsPluginOrigin = isPluginOrigin;
        }

        public static CommandResult Success(long elapsedMs)
        {
            return new(true, false, false, null, null, elapsedMs, false);
        }

        public static CommandResult Failure(string errorMessage, int? exitCode, long elapsedMs, bool isPluginOrigin = false)
        {
            return new(false, true, false, errorMessage, exitCode, elapsedMs, isPluginOrigin);
        }

        public static CommandResult Cancelled(string errorMessage, long elapsedMs)
        {
            return new(false, false, true, errorMessage, null, elapsedMs, false);
        }
    }
}
