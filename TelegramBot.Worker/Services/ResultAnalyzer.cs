using System.Diagnostics;
using System.Xml;
using System.Xml.Serialization;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Helpers;
using TelegramBot.Core.Models;
using TelegramBot.Worker.Helpers;
using TelegramBot.Worker.Schemas;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Анализирует результат выполнения команды: ResultFile от плагина или exit code.
/// </summary>
public sealed class ResultAnalyzer(CommandTaskFileStore taskFileStore, ILogger<ResultAnalyzer> logger)
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
        var (path, _) = taskFileStore.GetPaths(commandId, filePath);

        if (!File.Exists(path))
        {
            return (ResultFileReadStatus.NotFound, null, null);
        }

        for (var attempt = 0; attempt <= ResultFileReadRetryCount; attempt++)
        {
            try
            {
                using (var validationStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = XmlReader.Create(validationStream))
                {
                    var validationErrors = XmlContractValidator.ValidateResultFile(reader);
                    if (validationErrors.Count > 0)
                    {
                        logger.LogWarning(
                            "Plugin result file violates schema: id={CommandId}, errors={ErrorCount}",
                            commandId, validationErrors.Count);
                        RenameToBadFile(path);
                        return (ResultFileReadStatus.Invalid, null, "Plugin result file violates the BIM contract schema");
                    }
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var result = (ResultFile)ResultFileSerializer.Deserialize(stream)!;

                if (result.Status is ResultStatus.Done or ResultStatus.Failed or ResultStatus.Cancelled)
                {
                    // ProcessRunner removes the file after the outcome is persisted.
                    return (ResultFileReadStatus.Valid, result, null);
                }

                RenameToBadFile(path);
                return (ResultFileReadStatus.Invalid, null, $"Plugin result file has invalid status: {path}");
            }
            catch (Exception ex) when (ex is InvalidOperationException or XmlException)
            {
                if (await RetryReadAsync(attempt, ct))
                {
                    continue;
                }

                RenameToBadFile(path);
                logger.LogWarning("Plugin result file contains invalid XML: id={CommandId}", commandId);
                return (ResultFileReadStatus.Invalid, null, "Plugin result file contains invalid XML");
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
        if (cmd.CommandText is CommandCodes.MergeDwg)
        {
            return AnalyzeMergeDwgStatus(cmd, process, sw);
        }

        if (resultReadStatus == ResultFileReadStatus.Valid && result != null)
        {
            return DeterminePluginResult(cmd, result, sw);
        }

        if (resultReadStatus == ResultFileReadStatus.Invalid)
        {
            return CommandResult.Failure(resultReadError ?? "Invalid plugin result file", null);
        }

        // Fallback по exit code
        if (process.ExitCode == 0 && !CommandTraits.RequiresRevit(cmd.CommandText))
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

    /// <summary>
    /// MERGEDWG отвечает status JSON, а не ResultFile: exit code 0 без ответа успехом не считается.
    /// </summary>
    private CommandResult AnalyzeMergeDwgStatus(PendingCommand cmd, Process process, Stopwatch sw)
    {
        var (_, statusPath) = taskFileStore.GetMergeDwgPaths(cmd.CommandId, cmd.FilePath ?? string.Empty);
        var (status, readError) = MergeDwgBatchProtocol.TryReadStatus(statusPath);

        if (status is null)
        {
            logger.LogWarning(
                "MERGEDWG no status: id={Id}, corr={CorrelationId}, exit={ExitCode}, path={StatusPath}, err={Error}, ms={ElapsedMs}",
                cmd.CommandId, cmd.CorrelationId, process.ExitCode, statusPath, readError, sw.ElapsedMilliseconds);

            return CommandResult.Failure(
                $"{readError} (AutoCAD exit code {ExitCodeFormatter.Format(process.ExitCode)})",
                process.ExitCode);
        }

        if (status.Success)
        {
            logger.LogInformation(
                "MERGEDWG done: id={Id}, corr={CorrelationId}, save={SavePath}, ms={ElapsedMs}",
                cmd.CommandId, cmd.CorrelationId, status.SavePath ?? "<none>", sw.ElapsedMilliseconds);
            return CommandResult.Success();
        }

        var failureMessage = string.IsNullOrWhiteSpace(status.Message)
            ? "AutoBIMFusion MERGEDWG_BATCH reported failure"
            : status.Message;

        logger.LogWarning(
            "MERGEDWG fail: id={Id}, corr={CorrelationId}, msg={Message}, log={LogPath}, ms={ElapsedMs}",
            cmd.CommandId, cmd.CorrelationId, failureMessage, status.LogPath ?? "<none>", sw.ElapsedMilliseconds);

        return CommandResult.Failure(
            failureMessage,
            process.ExitCode,
            failureDisposition: CommandResult.FailureDisposition.PermanentPlugin);
    }

    /// <summary>Исход валидного ResultFile без процесса — recovery после сбоя записи БД.</summary>
    public CommandResult DeterminePluginResult(PendingCommand cmd, ResultFile result, Stopwatch sw)
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

        bool isRetryableRevitOpenFailure = IsRetryableRevitOpenFailure(cmd, result.ErrorDetails);
        if (isRetryableRevitOpenFailure)
        {
            logger.LogWarning(
                "Revit open failure is retryable: id={Id}, corr={CorrelationId}, cmd={Cmd}, attempt={Attempt}",
                cmd.CommandId, cmd.CorrelationId, cmd.CommandText, cmd.RetryCount + 1);
        }

        return CommandResult.Failure(
            result.ErrorMessage ?? "Plugin reported failure",
            exitCode: null,
            failureDisposition: isRetryableRevitOpenFailure
                ? CommandResult.FailureDisposition.RetryRevitOpenOnce
                : CommandResult.FailureDisposition.PermanentPlugin);
    }

    private static bool IsRetryableRevitOpenFailure(PendingCommand cmd, string? errorDetails)
    {
        return CommandTraits.RequiresRevit(cmd.CommandText)
            && errorDetails?.Contains("Autodesk.Revit.Exceptions.InternalException", StringComparison.OrdinalIgnoreCase) == true
            && errorDetails.Contains("UIApplication.OpenAndActivateDocument", StringComparison.OrdinalIgnoreCase);
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
        public FailureDisposition Disposition { get; }
        public string? ErrorMessage { get; }
        public string? WarningMessage { get; }
        public int? ExitCode { get; }

        private CommandResult(
            bool isSuccess,
            bool isFailure,
            bool isCancelled,
            string? errorMessage,
            int? exitCode,
            FailureDisposition disposition,
            string? warningMessage = null)
        {
            IsSuccess = isSuccess;
            IsFailure = isFailure;
            IsCancelled = isCancelled;
            ErrorMessage = errorMessage;
            ExitCode = exitCode;
            Disposition = disposition;
            WarningMessage = warningMessage;
        }

        public static CommandResult Success(string? warningMessage = null)
        {
            // Whitespace-only warnings are treated as absent — never persist them as ErrorMessage.
            warningMessage = string.IsNullOrWhiteSpace(warningMessage) ? null : warningMessage;
            return new(true, false, false, null, null, FailureDisposition.Classify, warningMessage: warningMessage);
        }

        public static CommandResult Failure(
            string errorMessage,
            int? exitCode,
            FailureDisposition failureDisposition = FailureDisposition.Classify)
        {
            return new(false, true, false, errorMessage, exitCode, failureDisposition);
        }

        public static CommandResult Cancelled(string errorMessage)
        {
            return new(false, false, true, errorMessage, null, FailureDisposition.Classify);
        }

        public enum FailureDisposition
        {
            Classify,
            PermanentPlugin,
            RetryRevitOpenOnce,
        }
    }
}
