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
/// Делегирует специализированным сервисам: ProcessStarter, OutputCollector, ResultAnalyzer.
/// </summary>
public sealed class ProcessRunner(
    CommandPreparer commandPreparer,
    ProcessStarter processStarter,
    OutputCollector outputCollector,
    ResultAnalyzer resultAnalyzer,
    CommandDataService commandDataService,
    SessionDataService sessionDataService,
    IOptions<WorkerOptions> workerOptions,
    ILogger<ProcessRunner> logger)
{
    private readonly WorkerOptions _workerOptions = workerOptions.Value;

    // Трекинг активных процессов для health-мониторинга и graceful shutdown
    private readonly ConcurrentDictionary<int, Process> _activeProcesses = new();
    private const int PerProcessKillTimeoutSeconds = 10;

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

        try
        {
            // Шаг 1: подготовка (валидация + BIM-резолвинг)
            var commandCfg = await commandPreparer.PrepareAsync(cmd, timeoutToken);
            if (commandCfg == null)
            {
                // PrepareAsync уже записал Failed в БД
                await NotifySessionCompletionAsync(cmd);
                return;
            }

            // Шаг 2: запуск процесса
            process = await StartProcessAsync(cmd, commandCfg, timeoutToken);

            // Шаг 3: ожидание и обработка ResultFile/exit code + stdout/stderr
            await WaitAndHandleResultAsync(cmd, process, sw, timeoutToken);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout — не shutdown
            await HandleTimeoutAsync(cmd, process, sw);
        }
        catch (OperationCanceledException)
        {
            // Shutdown — НЕ удаляем из _activeProcesses и НЕ диспозим процесс:
            // LogActiveProcessesOnShutdownAsync должен видеть все активные процессы до остановки.
            throw;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Для исключения при запуске процесса передаём exitCode = null — классификация по типу исключения
            await HandleFailureAsync(cmd, ex.Message, sw, ex: ex);
        }
        finally
        {
            // Очищаем temp-файлы этой попытки
            commandPreparer.CleanupTempFiles(cmd.CommandId, cmd.FilePath ?? string.Empty);

            // На shutdown не удаляем процесс из tracking'а — пусть LogActiveProcessesOnShutdownAsync его увидит.
            if (!ct.IsCancellationRequested)
            {
                _ = _activeProcesses.TryRemove(cmd.CommandId, out var removedProcess);
                removedProcess?.Dispose();
            }
        }
    }

    /// <summary>Запускает процесс по конфигурации команды.</summary>
    private async Task<Process> StartProcessAsync(PendingCommand cmd, CommandConfig commandCfg, CancellationToken ct)
    {
        var process = await processStarter.StartAsync(cmd, commandCfg, ct);
        _activeProcesses[cmd.CommandId] = process;
        _ = await commandDataService.MarkProcessStartedAndNotifyOnceAsync(
            cmd.CommandId, process.Id, cmd.SessionId, cmd.CorrelationId, cmd.UserId);
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
        var commandResult = resultAnalyzer.DetermineResult(cmd, resultReadStatus, result, resultReadError, process, sw);

        if (commandResult.IsSuccess)
        {
            // Done + non-empty Commands.ErrorMessage = plugin warningMessage (not a failure).
            _ = await commandDataService.UpdateCommandStatusAsync(
                cmd.CommandId,
                Statuses.Done,
                errorMessage: commandResult.WarningMessage);
            await NotifySessionCompletionAsync(cmd);
        }
        else if (commandResult.IsCancelled)
        {
            _ = await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Failed, errorMessage: commandResult.ErrorMessage);
            await NotifySessionCompletionAsync(cmd);
        }
        else if (commandResult.IsFailure)
        {
            await HandleFailureAsync(cmd, commandResult.ErrorMessage!, sw, commandResult.ExitCode,
                isPluginOrigin: commandResult.IsPluginOrigin);
        }
    }

    /// <summary>
    /// Обрабатывает таймаут процесса: убивает процесс, записывает Failed.
    /// </summary>
    /// <remarks>
    /// Таймаут intentionally идёт в Failed напрямую, минуя ErrorClassifier и retry-механизм
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

        _ = await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Failed,
            errorMessage: $"Process timed out after {_workerOptions.ProcessTimeoutMinutes} min");
        await NotifySessionCompletionAsync(cmd);
    }

    /// <summary>
    /// Планирует retry (для ProcessCrashError) или помечает команду как Failed (для InvalidFileError).
    /// </summary>
    private async Task HandleFailureAsync(PendingCommand cmd, string errorMessage, Stopwatch sw, int? exitCode = null, Exception? ex = null, bool isPluginOrigin = false)
    {
        sw.Stop();

        if (isPluginOrigin)
        {
            // Валидный result.xml status=Failed — плагин осознанно записал ошибку: permanent, retry бессмысленен.
            logger.LogInformation("Plugin permanent fail (no retry): cmd={Cmd}, id={Id}, corr={CorrelationId}, ms={ElapsedMs}, err={Msg}",
                cmd.CommandText, cmd.CommandId, cmd.CorrelationId, sw.ElapsedMilliseconds, errorMessage);
            _ = await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Failed, errorMessage: errorMessage);
            await NotifySessionCompletionAsync(cmd);
            return;
        }

        var isPermanent = ErrorClassifier.IsPermanentFailure(errorMessage, ex);

        // Лог решения классификатора — развилка retry/Failed: по exitCode, типу исключения или паттерну текста.
        logger.LogInformation("Classify: cmd={Cmd}, id={Id}, corr={CorrelationId}, attempt={Attempt}/{Max}, exit={ExitCode}, permanent={IsPermanent}, err={Msg}",
            cmd.CommandText, cmd.CommandId, cmd.CorrelationId, cmd.RetryCount + 1, _workerOptions.MaxRetries, ExitCodeFormatter.Format(exitCode), isPermanent, errorMessage);

        if (isPermanent)
        {
            _ = await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Failed, errorMessage: errorMessage);
            logger.LogError(ex, "Permanent fail: cmd={Cmd}, id={Id}, corr={CorrelationId}, exit={ExitCode}, ms={ElapsedMs}, err={Msg}",
                cmd.CommandText, cmd.CommandId, cmd.CorrelationId, ExitCodeFormatter.Format(exitCode), sw.ElapsedMilliseconds, errorMessage);
            await NotifySessionCompletionAsync(cmd);
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
            await NotifySessionCompletionAsync(cmd);
        }
        else
        {
            _ = await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Failed, errorMessage: errorMessage);
            logger.LogError(ex, "Fail after retries: cmd={Cmd}, id={Id}, corr={CorrelationId}, attempt={Attempt}, exit={ExitCode}, ms={ElapsedMs}, err={Msg}",
                cmd.CommandText, cmd.CommandId, cmd.CorrelationId, cmd.RetryCount + 1, ExitCodeFormatter.Format(exitCode), sw.ElapsedMilliseconds, errorMessage);
            await NotifySessionCompletionAsync(cmd);
        }
    }

    private async Task NotifySessionCompletionAsync(PendingCommand cmd)
    {
        try
        {
            var remainingInDb = await sessionDataService.CountPendingProcessingBySessionAsync(cmd.SessionId);
            if (remainingInDb > 0)
            {
                logger.LogDebug(
                    "Session {SessionId}: {Remaining} pending, skip notify, corr={CorrelationId}",
                    cmd.SessionId, remainingInDb, cmd.CorrelationId);
                return;
            }

            var notified = await sessionDataService.NotifySessionCompletedOnceAsync(cmd.SessionId, cmd.CorrelationId);
            if (!notified)
            {
                logger.LogDebug(
                    "Session {SessionId}: notify already sent, corr={CorrelationId}",
                    cmd.SessionId, cmd.CorrelationId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notify session fail: session={SessionId}, corr={CorrelationId}",
                cmd.SessionId, cmd.CorrelationId);
        }
    }
}

