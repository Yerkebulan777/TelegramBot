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

    // Трекинг активных процессов для health-мониторинга и graceful shutdown
    private readonly ConcurrentDictionary<int, Process> _activeProcesses = new();
    private const int PerProcessKillTimeoutSeconds = 10;
    private const int RevitOpenRetryDelaySeconds = 10;

    /// <summary>Снимок активных процессов для health-мониторинга.</summary>
    public IEnumerable<KeyValuePair<int, Process>> ActiveProcesses => _activeProcesses;

    /// <summary>
    /// Полный цикл выполнения одной команды: подготовка → запуск → ожидание → retry/fail.
    /// </summary>
    public async Task RunAsync(PendingCommand cmd, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        Process? process = null;

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
                // Shutdown — НЕ удаляем из _activeProcesses и НЕ диспозим процесс:
                // LogActiveProcessesOnShutdownAsync должен видеть все активные процессы до остановки.
                throw;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is not CommandPersistenceException)
            {
                // Для исключения при запуске процесса передаём exitCode = null — классификация по типу исключения
                await HandleFailureAsync(cmd, ex.Message, sw, ex: ex);
                return;
            }
        }
        catch (CommandPersistenceException)
        {
            // Keep result evidence and do not convert a database outage into a process retry.
            preserveEvidence = true;
            throw;
        }
        finally
        {
            // Очищаем temp-файлы этой попытки
            if (!preserveEvidence && !ct.IsCancellationRequested)
            {
                taskFileStore.Cleanup(cmd.CommandId, cmd.FilePath ?? string.Empty);
            }

            // На shutdown не удаляем процесс из tracking'а — пусть LogActiveProcessesOnShutdownAsync его увидит.
            if (!ct.IsCancellationRequested)
            {
                _ = _activeProcesses.TryRemove(cmd.CommandId, out var removedProcess);
                removedProcess?.Dispose();
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
            var gate = CommandTraits.GetLaunchGate(cmd.CommandText);
            if (gate == ProcessLaunchGateKind.None)
            {
                _ = process.Start();
            }
            else
            {
                await launchGate.StartAsync(process, gate, cmd.CommandId, ct);
            }

            logger.LogInformation(
                "Process started: cmd={Cmd}, id={Id}, corr={CorrelationId}, pid={Pid}, attempt={Attempt}",
                cmd.CommandText, cmd.CommandId, cmd.CorrelationId, process.Id, cmd.RetryCount + 1);
        }
        catch
        {
            process.Dispose();
            throw;
        }

        _activeProcesses[cmd.CommandId] = process;
        _ = await commandDataService.MarkProcessStartedAndNotifyOnceAsync(
            cmd.CommandId, process.Id);
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
            // Sync WaitForExit после WaitForExitAsync — НЕ ошибка и НЕ sync-over-async в вредном смысле.
            // Это рекомендуемый .NET-приём для redirected output: после асинхронного выхода процесса
            // sync-перегрузка дочитывает остатки stdout/stderr-буферов, гарантируя, что OutputDataReceived
            // успел отстрелять до разбора ResultFile. НЕ заменять на один только WaitForExitAsync.
            process.WaitForExit();
        }
        finally
        {
            outputSubscription.Dispose();
        }

        sw.Stop();
        outputCollector.LogOutput(cmd, outputSubscription.Output, outputSubscription.Error, outputSubscription.OutputTruncated, outputSubscription.ErrorTruncated);

        // Определяем результат
        var (resultReadStatus, result, resultReadError) = await resultAnalyzer.TryReadResultFileAsync(
            cmd.CommandId,
            cmd.FilePath ?? string.Empty,
            ct);

        try
        {
            var commandResult = resultAnalyzer.DetermineResult(cmd, resultReadStatus, result, resultReadError, process, sw);
            if (commandResult.IsSuccess)
            {
                // Done + non-empty Commands.ErrorMessage = plugin warningMessage (not a failure).
                await CompleteCommandAsync(
                    cmd,
                    Statuses.Done,
                    errorMessage: commandResult.WarningMessage);
                return;
            }
            else if (commandResult.IsCancelled)
            {
                await CompleteCommandAsync(cmd, Statuses.Failed, errorMessage: commandResult.ErrorMessage);
                return;
            }
            else if (commandResult.IsFailure)
            {
                await HandleFailureAsync(cmd, commandResult.ErrorMessage!, sw, commandResult.ExitCode,
                    failureDisposition: commandResult.Disposition);
            }

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
                int newRetryCount = await commandDataService.ScheduleRetryAsync(cmd.CommandId, nextRetryAt, errorMessage);
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
            var newRetryCount = await commandDataService.ScheduleRetryAsync(cmd.CommandId, nextRetryAt, errorMessage);
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
