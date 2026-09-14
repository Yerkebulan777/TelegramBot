using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Helpers;
using TelegramBot.Core.Models;
using TelegramBot.Data;
using TelegramBot.Worker.Helpers;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Координирует запуск, ожидание и обработку результатов внешних процессов.
/// </summary>
public sealed class ProcessRunner(
    CommandPreparer commandPreparer,
    CommandTaskFileStore taskFileStore,
    ProcessLaunchGate launchGate,
    OutputCollector outputCollector,
    ResultAnalyzer resultAnalyzer,
    RevitTemporaryDirectoryCleaner temporaryDirectoryCleaner,
    CommandDataService commandDataService,
    IOptions<WorkerOptions> workerOptions,
    ILogger<ProcessRunner> logger)
{
    private readonly WorkerOptions _workerOptions = workerOptions.Value;

    private sealed class TrackedCommand(long lease)
    {
        public long Lease { get; } = lease;
        public Process? Process { get; set; }
    }

    private readonly ConcurrentDictionary<int, TrackedCommand> _tracked = new();
    private const int PerProcessKillTimeoutSeconds = 10;
    private const int StreamDrainTimeoutMs = 10_000;
    private const int ShutdownLeaseRetryDelaySeconds = 15;
    private const int RevitOpenRetryDelaySeconds = 10;

    /// <summary>Снимок запущенных процессов для health-мониторинга и shutdown kill.</summary>
    public IEnumerable<KeyValuePair<int, Process>> ActiveProcesses
    {
        get
        {
            foreach (var (commandId, tracked) in _tracked)
            {
                if (tracked.Process is { } process)
                {
                    yield return new KeyValuePair<int, Process>(commandId, process);
                }
            }
        }
    }

    /// <summary>
    /// Полный цикл выполнения одной команды: подготовка → запуск → ожидание → retry/fail.
    /// </summary>
    public async Task RunAsync(PendingCommand cmd, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        Process? process = null;
        _tracked[cmd.CommandId] = new TrackedCommand(cmd.Lease);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(_workerOptions.ProcessTimeoutMinutes));
        var timeoutToken = timeoutCts.Token;

        var preserveEvidence = false;
        try
        {
            try
            {
                // Шаг 1: подготовка (валидация + BIM-резолвинг)
                var preparation = await commandPreparer.PrepareAsync(cmd, timeoutToken);
                if (!preparation.IsReady)
                {
                    await CompleteCommandAsync(cmd, Statuses.Failed, preparation.ErrorMessage);
                    return;
                }

                if (await TryCompleteFromExistingPluginResultAsync(cmd, sw, timeoutToken))
                {
                    return;
                }

                // Шаг 2: запуск процесса
                process = await StartProcessAsync(cmd, preparation.GetConfiguration(), timeoutToken);

                // Шаг 3: ожидание и обработка ResultFile/exit code + stdout/stderr
                await WaitAndHandleResultAsync(cmd, process, sw, timeoutToken);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout — не shutdown
                await HandleTimeoutAsync(cmd, process, sw);
                return;
            }
            catch (OperationCanceledException)
            {
                // Shutdown: tracking остаётся, пока CommandExecutionService не убьёт процессы и не отпустит lease.
                throw;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is not CommandPersistenceException)
            {
                // Для исключения при запуске процесса передаём exitCode = null — классификация по типу исключения
                await HandleFailureAsync(cmd, ex.Message, sw, ex: ex);
                return;
            }
        }
        catch (CommandPersistenceException ex)
        {
            preserveEvidence = true;
            logger.LogError(ex, "Persistence after BIM work: id={CommandId}, corr={CorrelationId}",
                cmd.CommandId, cmd.CorrelationId);
            await ReleaseLeaseAfterPersistenceFailureAsync(cmd);
        }
        finally
        {
            // Очищаем temp-файлы этой попытки
            if (!preserveEvidence && !ct.IsCancellationRequested)
            {
                taskFileStore.Cleanup(cmd.CommandId, cmd.FilePath ?? string.Empty);
            }

            // На shutdown tracking держит CommandExecutionService (kill + release lease).
            if (!ct.IsCancellationRequested && _tracked.TryRemove(cmd.CommandId, out var tracked))
            {
                tracked.Process?.Dispose();
            }
        }
    }

    /// <summary>Запускает процесс по конфигурации команды и регистрирует его в tracking.</summary>
    private async Task<Process> StartProcessAsync(PendingCommand cmd, CommandConfig commandCfg, CancellationToken ct)
    {
        var (resultFilePath, taskFilePath) = taskFileStore.GetPaths(cmd.CommandId, cmd.FilePath ?? string.Empty);
        if (File.Exists(resultFilePath))
        {
            File.Move(resultFilePath, resultFilePath + ".previous", overwrite: true);
            logger.LogWarning("Previous result retained before new attempt: id={CommandId}", cmd.CommandId);
        }
        if (!taskFileStore.Create(cmd))
        {
            throw new IOException(
                $"Failed to write task file in TaskDirectory '{taskFilePath}'. " +
                $"AddIn cannot proceed without the task file. Check FileSystem:TaskDirectory permissions, disk space, and antivirus.");
        }

        var startInfo = commandPreparer.CreateProcessStartInfo(cmd, commandCfg);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            await StartAndTrackAsync(cmd, process, ct);

            logger.LogInformation(
                "Process started: cmd={Cmd}, id={Id}, corr={CorrelationId}, pid={Pid}, attempt={Attempt}",
                cmd.CommandText, cmd.CommandId, cmd.CorrelationId, process.Id, cmd.RetryCount + 1);
        }
        catch
        {
            process.Dispose();
            throw;
        }

        using var sqlCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sqlCts.CancelAfter(TimeSpan.FromSeconds(5));
        _ = await commandDataService.MarkProcessStartedAndNotifyOnceAsync(
            cmd.CommandId, process.Id, sqlCts.Token);
        return process;
    }

    /// <summary>Ожидает завершения процесса, собирает stdout/stderr с ограничением размера.
    /// Использует потоковую обработку для предотвращения переполнения памяти.
    /// После выхода процесса пробует прочитать result-файл от плагина.
    /// </summary>
    private async Task WaitAndHandleResultAsync(PendingCommand cmd, Process process, Stopwatch sw, CancellationToken ct)
    {
        using var outputSubscription = new OutputCollector.ProcessOutputCapture(process);

        try
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(ct);
            await DrainRedirectedOutputAsync(cmd, process);
        }
        finally
        {
            outputSubscription.Dispose();
        }

        sw.Stop();
        outputCollector.LogOutput(cmd, outputSubscription.Output, outputSubscription.Error, outputSubscription.OutputTruncated, outputSubscription.ErrorTruncated);

        var (resultReadStatus, result, resultReadError) = await resultAnalyzer.TryReadResultFileAsync(
            cmd.CommandId,
            cmd.FilePath ?? string.Empty,
            ct);

        try
        {
            var commandResult = await resultAnalyzer.DetermineResultAsync(
                cmd, resultReadStatus, result, resultReadError, process, sw, ct);
            await ApplyCommandResultAsync(cmd, commandResult, sw);
            return;
        }
        finally
        {
            temporaryDirectoryCleaner.Schedule(
                result?.TemporaryDirectoryPath,
                cmd.FilePath,
                cmd.CommandId);
        }
    }

    /// <summary>
    /// Обрабатывает таймаут процесса: убивает процесс, записывает Failed.
    /// </summary>
    /// <remarks>
    /// Таймаут intentionally идёт в Failed напрямую, минуя классификатор и retry-механизм
    /// (в отличие от crash-исключения, который ретраится). Причина: <see cref="WorkerOptions.ProcessTimeoutMinutes"/>
    /// по умолчанию 180 минут (3 ч) — повтор такой задачи ещё 5 раз обойдётся в 15 часов CPU.
    /// Типичные причины таймаута (зависший сетевой диск, modal dialog) классифицируются как transient,
    /// но стоимость retry непропорциональна выгоде. Если потребуется retry-семантика для таймаута —
    /// пускать эту ветку через HandleFailureAsync (isPermanent: false).
    /// </remarks>
    private async Task HandleTimeoutAsync(PendingCommand cmd, Process? process, Stopwatch sw)
    {
        sw.Stop();

        if (process != null && !process.HasExited)
        {
            logger.LogWarning("Kill timeout: id={Id}, corr={CorrelationId}, pid={Pid}, elapsed={Elapsed:F1}s",
                cmd.CommandId, cmd.CorrelationId, process.Id, sw.Elapsed.TotalSeconds);
            _ = await ProcessKillHelper.KillAsync(process, TimeSpan.FromSeconds(PerProcessKillTimeoutSeconds), logger, cmd.CommandId);
        }

        logger.LogError("Timeout: id={CommandId}, cmd={Cmd}, corr={CorrelationId}, to={Timeout}m, elapsed={Elapsed:F1}s",
            cmd.CommandId, cmd.CommandText, cmd.CorrelationId, _workerOptions.ProcessTimeoutMinutes, sw.Elapsed.TotalSeconds);

        await CompleteCommandAsync(cmd, Statuses.Failed,
            errorMessage: $"Process timed out after {_workerOptions.ProcessTimeoutMinutes} min");
    }

    /// <summary>
    /// Sync WaitForExit после WaitForExitAsync дочитывает redirected stdout/stderr.
    /// Без timeout дочерние процессы могут держать pipe бесконечно.
    /// </summary>
    private async Task DrainRedirectedOutputAsync(PendingCommand cmd, Process process)
    {
        if (process.WaitForExit(StreamDrainTimeoutMs))
        {
            return;
        }

        logger.LogWarning(
            "Process output drain timed out: id={CommandId}, pid={Pid}",
            cmd.CommandId, process.Id);
        _ = await ProcessKillHelper.KillAsync(
            process,
            TimeSpan.FromSeconds(PerProcessKillTimeoutSeconds),
            logger,
            cmd.CommandId);
        if (!process.WaitForExit(StreamDrainTimeoutMs))
        {
            logger.LogWarning(
                "Process output drain still blocked after kill: id={CommandId}",
                cmd.CommandId);
        }
    }

    /// <summary>Убивает tracked-процесс команды (диалоги, которые DialogDismisser не закрыл).</summary>
    public async Task KillTrackedProcessAsync(int commandId, CancellationToken cancellationToken = default)
    {
        if (!_tracked.TryGetValue(commandId, out var tracked) || tracked.Process is not { } process)
        {
            return;
        }

        if (process.HasExited)
        {
            return;
        }

        logger.LogWarning("Kill tracked process: id={CommandId}, pid={Pid}", commandId, process.Id);
        _ = await ProcessKillHelper.KillAsync(
            process,
            TimeSpan.FromSeconds(PerProcessKillTimeoutSeconds),
            logger,
            commandId,
            cancellationToken);
    }

    /// <summary>
    /// После kill на shutdown возвращает оставшиеся claimed команды в pending без инкремента RetryCount.
    /// </summary>
    public async Task ReleaseClaimedLeasesOnShutdownAsync()
    {
        var nextRetryAt = DateTime.UtcNow.AddSeconds(ShutdownLeaseRetryDelaySeconds);
        foreach (var (commandId, tracked) in _tracked.ToArray())
        {
            try
            {
                var released = await commandDataService.ReleaseClaimedLeaseAsync(
                    commandId, tracked.Lease, nextRetryAt, "Worker shutdown");
                if (released)
                {
                    logger.LogInformation("Shutdown released lease: id={CommandId}", commandId);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Shutdown lease release failed: id={CommandId}", commandId);
            }
            finally
            {
                _ = _tracked.TryRemove(commandId, out _);
            }
        }
    }

    /// <summary>
    /// Планирует retry (для ProcessCrashError) или помечает команду как Failed (для InvalidFileError).
    /// </summary>
    private async Task HandleFailureAsync(
        PendingCommand cmd,
        string errorMessage,
        Stopwatch sw,
        int? exitCode = null,
        Exception? ex = null,
        ResultAnalyzer.CommandResult.FailureDisposition failureDisposition = ResultAnalyzer.CommandResult.FailureDisposition.Classify)
    {
        sw.Stop();

        if (failureDisposition == ResultAnalyzer.CommandResult.FailureDisposition.RetryRevitOpenOnce)
        {
            if (cmd.RetryCount == 0)
            {
                DateTime nextRetryAt = DateTime.UtcNow.AddSeconds(RevitOpenRetryDelaySeconds);
                var newRetryCount = await TryScheduleRetryAsync(cmd, nextRetryAt, errorMessage);
                if (newRetryCount is null)
                {
                    return;
                }
                logger.LogWarning(
                    "Revit open retry scheduled: cmd={Cmd}, id={Id}, corr={CorrelationId}, attempt={Attempt}, retryAt={Next:O}, ms={ElapsedMs}, err={Msg}",
                    cmd.CommandText, cmd.CommandId, cmd.CorrelationId, newRetryCount, nextRetryAt, sw.ElapsedMilliseconds, errorMessage);
                return;
            }

            const string retryFailureSuffix = " Автоматическая повторная попытка открытия модели также завершилась неудачей.";
            string finalErrorMessage = errorMessage + retryFailureSuffix;
            logger.LogError(
                "Revit open retry exhausted: cmd={Cmd}, id={Id}, corr={CorrelationId}, attempt={Attempt}, ms={ElapsedMs}, err={Msg}",
                cmd.CommandText, cmd.CommandId, cmd.CorrelationId, cmd.RetryCount + 1, sw.ElapsedMilliseconds, finalErrorMessage);
            await CompleteCommandAsync(cmd, Statuses.Failed, errorMessage: finalErrorMessage);
            return;
        }

        if (failureDisposition == ResultAnalyzer.CommandResult.FailureDisposition.PermanentPlugin)
        {
            // Валидный result.xml status=Failed — плагин осознанно записал ошибку: permanent, retry бессмысленен.
            logger.LogInformation("Plugin permanent fail (no retry): cmd={Cmd}, id={Id}, corr={CorrelationId}, ms={ElapsedMs}, err={Msg}",
                cmd.CommandText, cmd.CommandId, cmd.CorrelationId, sw.ElapsedMilliseconds, errorMessage);
            await CompleteCommandAsync(cmd, Statuses.Failed, errorMessage: errorMessage);
            return;
        }

        var isPermanent = IsPermanentFailure(errorMessage, ex);

        // Лог решения классификатора — развилка retry/Failed: по exitCode, типу исключения или паттерну текста.
        logger.LogInformation("Classify: cmd={Cmd}, id={Id}, corr={CorrelationId}, attempt={Attempt}/{Max}, exit={ExitCode}, permanent={IsPermanent}, err={Msg}",
            cmd.CommandText, cmd.CommandId, cmd.CorrelationId, cmd.RetryCount + 1, _workerOptions.MaxRetries, ExitCodeFormatter.Format(exitCode), isPermanent, errorMessage);

        if (isPermanent)
        {
            await CompleteCommandAsync(cmd, Statuses.Failed, errorMessage: errorMessage);
            logger.LogError(ex, "Permanent fail: cmd={Cmd}, id={Id}, corr={CorrelationId}, exit={ExitCode}, ms={ElapsedMs}, err={Msg}",
                cmd.CommandText, cmd.CommandId, cmd.CorrelationId, ExitCodeFormatter.Format(exitCode), sw.ElapsedMilliseconds, errorMessage);
            return;
        }
        else if (cmd.RetryCount < _workerOptions.MaxRetries)
        {
            var baseDelay = _workerOptions.RetryDelayBaseSeconds * (1 << cmd.RetryCount);
            var jitterSeconds = Random.Shared.Next(0, _workerOptions.RetryDelayBaseSeconds);
            var nextRetryAt = DateTime.UtcNow.AddSeconds(baseDelay + jitterSeconds);
            var newRetryCount = await TryScheduleRetryAsync(cmd, nextRetryAt, errorMessage);
            if (newRetryCount is null)
            {
                return;
            }
            logger.LogWarning(ex, "Retry: cmd={Cmd}, id={Id}, corr={CorrelationId}, attempt={Attempt}/{Max}, retryAt={Next:O}, exit={ExitCode}, ms={ElapsedMs}, err={Msg}",
                cmd.CommandText, cmd.CommandId, cmd.CorrelationId, newRetryCount, _workerOptions.MaxRetries,
                nextRetryAt, ExitCodeFormatter.Format(exitCode), sw.ElapsedMilliseconds, errorMessage);
            return;
        }
        else
        {
            await CompleteCommandAsync(cmd, Statuses.Failed, errorMessage: errorMessage);
            logger.LogError(ex, "Fail after retries: cmd={Cmd}, id={Id}, corr={CorrelationId}, attempt={Attempt}, exit={ExitCode}, ms={ElapsedMs}, err={Msg}",
                cmd.CommandText, cmd.CommandId, cmd.CorrelationId, cmd.RetryCount + 1, ExitCodeFormatter.Format(exitCode), sw.ElapsedMilliseconds, errorMessage);
            return;
        }
    }

    private async Task CompleteCommandAsync(PendingCommand command, string status, string? errorMessage = null)
    {
        var notified = await commandDataService.CompleteCommandAndNotifyAsync(command, status, errorMessage: errorMessage);
        logger.LogDebug(
            "Terminal command persisted: id={CommandId}, status={Status}, sessionCompletionNotified={SessionCompletionNotified}",
            command.CommandId, status, notified);
    }

    private void TrackStartedProcess(int commandId, Process process)
    {
        if (_tracked.TryGetValue(commandId, out var tracked))
        {
            tracked.Process = process;
        }
    }

    private async Task StartAndTrackAsync(PendingCommand cmd, Process process, CancellationToken ct)
    {
        void Start()
        {
            _ = process.Start();
            TrackStartedProcess(cmd.CommandId, process);
        }

        var gate = CommandTraits.GetLaunchGate(cmd.CommandText);
        if (gate == ProcessLaunchGateKind.None)
        {
            Start();
            return;
        }

        await launchGate.StartAsync(gate, cmd.CommandId, ct, Start);
    }

    private async Task ReleaseLeaseAfterPersistenceFailureAsync(PendingCommand cmd)
    {
        try
        {
            var released = await commandDataService.ReleaseClaimedLeaseAsync(
                cmd.CommandId,
                cmd.Lease,
                DateTime.UtcNow.AddSeconds(ShutdownLeaseRetryDelaySeconds),
                "Command persistence failed");
            if (released)
            {
                logger.LogInformation("Released lease after persistence failure: id={CommandId}", cmd.CommandId);
            }
        }
        catch (Exception releaseEx)
        {
            logger.LogWarning(releaseEx, "Lease release after persistence failure failed: id={CommandId}", cmd.CommandId);
        }
    }

    /// <summary>
    /// Если предыдущая попытка уже записала валидный ResultFile (CPE или crash после плагина),
    /// закрываем команду без нового Process.Start.
    /// </summary>
    private async Task<bool> TryCompleteFromExistingPluginResultAsync(
        PendingCommand cmd, Stopwatch sw, CancellationToken ct)
    {
        if (cmd.CommandText is CommandCodes.MergeDwg)
        {
            return false;
        }

        var (resultReadStatus, result, resultReadError) = await resultAnalyzer.TryReadResultFileAsync(
            cmd.CommandId,
            cmd.FilePath ?? string.Empty,
            ct);
        if (resultReadStatus != ResultAnalyzer.ResultFileReadStatus.Valid || result is null)
        {
            return false;
        }

        logger.LogInformation(
            "Completing from existing ResultFile without relaunch: id={CommandId}, corr={CorrelationId}",
            cmd.CommandId, cmd.CorrelationId);
        await ApplyCommandResultAsync(cmd, resultAnalyzer.DeterminePluginResult(cmd, result, sw), sw);
        temporaryDirectoryCleaner.Schedule(result.TemporaryDirectoryPath, cmd.FilePath, cmd.CommandId);
        return true;
    }

    private async Task ApplyCommandResultAsync(
        PendingCommand cmd, ResultAnalyzer.CommandResult commandResult, Stopwatch sw)
    {
        if (commandResult.IsSuccess)
        {
            await CompleteCommandAsync(cmd, Statuses.Done, errorMessage: commandResult.WarningMessage);
            return;
        }

        if (commandResult.IsCancelled)
        {
            await CompleteCommandAsync(cmd, Statuses.Failed, errorMessage: commandResult.ErrorMessage);
            return;
        }

        if (commandResult.IsFailure)
        {
            await HandleFailureAsync(
                cmd,
                commandResult.ErrorMessage!,
                sw,
                commandResult.ExitCode,
                failureDisposition: commandResult.Disposition);
        }
    }

    private async Task<int?> TryScheduleRetryAsync(PendingCommand cmd, DateTime nextRetryAt, string errorMessage)
    {
        var retryCount = await commandDataService.ScheduleRetryAsync(
            cmd.CommandId, cmd.Lease, nextRetryAt, errorMessage);
        if (retryCount is null)
        {
            logger.LogWarning(
                "Retry not scheduled: id={CommandId} (lease mismatch or not processing)",
                cmd.CommandId);
        }

        return retryCount;
    }

    private static readonly string[] PermanentFailurePatterns =
    [
        "not found",
        "no such file",
        "cannot open file",
        "access is denied",
        "access denied",
        "invalid file",
        "file does not exist",
        "permission denied",
        "path not found",
        "invalid file path",
        "unsupported command:",
        "notimplemented:",
        "no such directory",
        "cannot access",
        "файл не найден",
        "не удается найти указанный файл",
        "не удаётся найти указанный файл",
        "путь не найден",
        "отказано в доступе",
        "доступ запрещен",
        "доступ запрещён",
        "нет доступа",
        "недопустимый файл",
        "неверный формат файла",
        "невозможно открыть файл",
    ];

    private static bool IsPermanentFailure(string errorMessage, Exception? exception)
    {
        if (exception is FileNotFoundException or DirectoryNotFoundException
            or UnauthorizedAccessException or PathTooLongException)
        {
            return true;
        }

        var message = errorMessage.ToLowerInvariant();
        return PermanentFailurePatterns.Any(message.Contains);
    }

}
