using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Запускает внешний процесс, отслеживает его выполнение, обрабатывает stdout/stderr,
/// таймауты, retry и финальный статус команды.
/// Владеет словарём активных процессов <c>_activeProcesses</c> для health-мониторинга.
/// </summary>
public sealed class ProcessRunner(
    CommandPreparer commandPreparer,
    SessionCompletionTracker sessionCompletionTracker,
    ICommandDataService commandDataService,
    IOptions<WorkerOptions> workerOptions,
    ILogger<ProcessRunner> logger)
{
    private readonly WorkerOptions _workerOptions = workerOptions.Value;

    // Трекинг активных процессов для health-мониторинга и graceful shutdown
    private readonly ConcurrentDictionary<int, Process> _activeProcesses = new();

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
                await sessionCompletionTracker.OnCommandCompletedAsync(cmd);
                return;
            }

            // Шаг 2: запуск процесса
            process = await StartProcessAsync(cmd, commandCfg);

            // Шаг 3: ожидание и обработка результата
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
        catch (Exception ex)
        {
            await HandleFailureAsync(cmd, ex.Message, ex, sw);
        }
        finally
        {
            // На shutdown не удаляем процесс из tracking'а — пусть LogActiveProcessesOnShutdownAsync его увидит.
            if (!ct.IsCancellationRequested)
            {
                _ = _activeProcesses.TryRemove(cmd.CommandId, out var removedProcess);
                removedProcess?.Dispose();
            }

            logger.LogDebug("Completed: id={Id}, correlationId={CorrelationId}, command={Cmd}, elapsedMs={ElapsedMs}",
                cmd.CommandId, cmd.CorrelationId, cmd.CommandText, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>Запускает процесс по конфигурации команды.</summary>
    private async Task<Process> StartProcessAsync(PendingCommand cmd, CommandConfig commandCfg)
    {
        var startInfo = CommandPreparer.CreateProcessStartInfo(cmd, commandCfg);

        logger.LogInformation("Command start: id={Id}, correlationId={CorrelationId}, command={Cmd}, attempt={Attempt}",
            cmd.CommandId, cmd.CorrelationId, cmd.CommandText, cmd.RetryCount + 1);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _ = process.Start();
        _activeProcesses[cmd.CommandId] = process;
        _ = await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Processing, process.Id);

        return process;
    }

    /// <summary>Ожидает завершения процесса, собирает stdout/stderr, определяет финальный статус.</summary>
    private async Task WaitAndHandleResultAsync(PendingCommand cmd, Process process, Stopwatch sw, CancellationToken ct)
    {
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

#pragma warning disable VSTHRD003
        await process.WaitForExitAsync(ct);
#pragma warning restore VSTHRD003

        LogProcessOutput(cmd, outputBuilder, errorBuilder);
        sw.Stop();

        if (process.ExitCode == 0)
        {
            _ = await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Done);
            logger.LogInformation("Command done: id={Id}, correlationId={CorrelationId}, command={Cmd}, elapsedMs={ElapsedMs}",
                cmd.CommandId, cmd.CorrelationId, cmd.CommandText, sw.ElapsedMilliseconds);
            await sessionCompletionTracker.OnCommandCompletedAsync(cmd);
        }
        else
        {
            var errorMessage = $"Process exited with code {process.ExitCode}";
            logger.LogWarning("Command exit: id={Id}, correlationId={CorrelationId}, command={Cmd}, exitCode={ExitCode}, elapsedMs={ElapsedMs}",
                cmd.CommandId, cmd.CorrelationId, cmd.CommandText, process.ExitCode, sw.ElapsedMilliseconds);
            await HandleFailureAsync(cmd, errorMessage, null, sw);
        }
    }

    /// <summary>Обрабатывает таймаут процесса: убивает процесс, записывает Failed.</summary>
    private async Task HandleTimeoutAsync(PendingCommand cmd, Process? process, Stopwatch sw)
    {
        sw.Stop();

        if (process != null && !process.HasExited)
        {
            logger.LogWarning("Killing timed-out process: commandId={Id}, correlationId={CorrelationId}, pid={Pid}, elapsed={Elapsed:F1}s",
                cmd.CommandId, cmd.CorrelationId, process.Id, sw.Elapsed.TotalSeconds);
            process.Kill(entireProcessTree: true);
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
        }

        logger.LogError("Command {CommandId} ({Cmd}) timed out: correlationId={CorrelationId}, timeout={Timeout} min, elapsed={Elapsed:F1}s",
            cmd.CommandId, cmd.CommandText, cmd.CorrelationId, _workerOptions.ProcessTimeoutMinutes, sw.Elapsed.TotalSeconds);

        _ = await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Failed,
            errorMessage: $"Process timed out after {_workerOptions.ProcessTimeoutMinutes} min");
        await sessionCompletionTracker.OnCommandCompletedAsync(cmd);
    }

    /// <summary>Планирует retry или помечает команду как Failed.</summary>
    private async Task HandleFailureAsync(PendingCommand cmd, string errorMessage, Exception? ex, Stopwatch sw)
    {
        sw.Stop();

        if (cmd.RetryCount < _workerOptions.MaxRetries)
        {
            var nextRetryAt = DateTime.UtcNow.AddSeconds(
                _workerOptions.RetryDelayBaseSeconds * (1 << cmd.RetryCount));
            var newRetryCount = await commandDataService.ScheduleRetryAsync(
                cmd.CommandId, nextRetryAt, errorMessage);
            logger.LogWarning(ex, "Command {Cmd} ({Id}) failed: correlationId={CorrelationId}, attempt={Attempt}/{Max}, retryAt={Next}, error={Msg}",
                cmd.CommandText, cmd.CommandId, cmd.CorrelationId, newRetryCount, _workerOptions.MaxRetries,
                nextRetryAt.ToString("O"), errorMessage);
            await sessionCompletionTracker.OnCommandCompletedAsync(cmd);
        }
        else
        {
            _ = await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Failed, errorMessage: errorMessage);
            logger.LogError(ex, "Command {Cmd} ({Id}) failed after attempts: correlationId={CorrelationId}, attempt={Attempt}, error={Msg}",
                cmd.CommandText, cmd.CommandId, cmd.CorrelationId, cmd.RetryCount + 1, errorMessage);
            await sessionCompletionTracker.OnCommandCompletedAsync(cmd);
        }
    }

    /// <summary>Логирует stdout и stderr процесса.</summary>
    private void LogProcessOutput(PendingCommand cmd, StringBuilder outputBuilder, StringBuilder errorBuilder)
    {
        if (outputBuilder.Length > 0)
        {
            logger.LogInformation("Output [{Cmd} {Id} {CorrelationId}]: {Output}",
                cmd.CommandText, cmd.CommandId, cmd.CorrelationId, TruncateOutput(outputBuilder));
        }

        if (errorBuilder.Length > 0)
        {
            logger.LogWarning("Stderr [{Cmd} {Id} {CorrelationId}]: {Error}",
                cmd.CommandText, cmd.CommandId, cmd.CorrelationId, TruncateOutput(errorBuilder));
        }
    }

    /// <summary>Обрезает вывод до 4 КБ для предотвращения раздувания логов.</summary>
    private static string TruncateOutput(StringBuilder builder)
    {
        const int maxLength = 4096;
        return builder.Length > maxLength
            ? builder.ToString(0, maxLength) + $"\n... (truncated, total {builder.Length} chars)"
            : builder.ToString(0, builder.Length);
    }
}
