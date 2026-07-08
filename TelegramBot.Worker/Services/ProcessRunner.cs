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
        await processStarter.UpdateProcessStatusAsync(cmd.CommandId, cmd.SessionId, cmd.CorrelationId, cmd.UserId, process.Id);
        return process;
    }

    /// <summary>Ожидает завершения процесса, собирает stdout/stderr с ограничением размера.
    /// Использует потоковую обработку для предотвращения переполнения памяти.
    /// После выхода процесса пробует прочитать result-файл от плагина.
    /// </summary>
    private async Task WaitAndHandleResultAsync(PendingCommand cmd, Process process, Stopwatch sw, CancellationToken ct)
    {
        using var outputSubscription = outputCollector.SetupProcessOutput(process);

        try
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(ct);
            process.WaitForExit();
        }
        finally
        {
            outputSubscription.Dispose();
        }

        sw.Stop();
        outputCollector.LogOutput(cmd, outputSubscription.Output, outputSubscription.Error, outputSubscription.OutputTruncated, outputSubscription.ErrorTruncated);

        // Определяем результат
        var resultReadStatus = resultAnalyzer.TryReadResultFile(cmd.CommandId, cmd.FilePath ?? string.Empty, out var result, out var resultReadError);
        var commandResult = resultAnalyzer.DetermineResult(cmd, resultReadStatus, result, resultReadError, process, sw);

        if (commandResult.IsSuccess)
        {
            _ = await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Done);
            await NotifySessionCompletionAsync(cmd);
        }
        else if (commandResult.IsCancelled)
        {
            _ = await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Failed, errorMessage: commandResult.ErrorMessage);
            await NotifySessionCompletionAsync(cmd);
        }
        else if (commandResult.IsFailure)
        {
            await HandleFailureAsync(cmd, commandResult.ErrorMessage!, sw, commandResult.ExitCode);
        }
    }

    /// <summary>Обрабатывает таймаут процесса: убивает процесс, записывает Failed.</summary>
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
    private async Task HandleFailureAsync(PendingCommand cmd, string errorMessage, Stopwatch sw, int? exitCode = null, Exception? ex = null)
    {
        sw.Stop();

        var isPermanent = ErrorClassifier.IsPermanentFailure(errorMessage, exitCode, _workerOptions.PermanentFailureExitCodes, ex);

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

