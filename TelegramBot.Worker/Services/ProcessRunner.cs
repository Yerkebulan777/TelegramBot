using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Data;
using TelegramBot.Worker.Helpers;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Запускает внешний процесс, отслеживает его выполнение, обрабатывает stdout/stderr,
/// таймауты, retry и финальный статус команды.
/// Результат выполнения определяется по exit code процесса.
/// Если плагин написал <c>result_{{CommandId}}.json</c> — статус берётся из него.
/// Владеет словарём активных процессов <c>_activeProcesses</c> для health-мониторинга.
/// Оптимизация: потоковая обработка stdout/stderr с ограничением 64KB для предотвращения переполнения памяти.
/// </summary>
public sealed class ProcessRunner(
    CommandPreparer commandPreparer,
    SessionCompletionTracker sessionCompletionTracker,
    CommandDataService commandDataService,
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

        // Уникальный токен попытки: предотвращает конфликты между retry одной команды
        // и атаки с предсказуемыми именами файлов.
        var attemptToken = Guid.NewGuid().ToString("N");

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
            process = await StartProcessAsync(cmd, commandCfg, attemptToken);

            // Шаг 3: ожидание и обработка результата (exit code + stdout/stderr)
            await WaitAndHandleResultAsync(cmd, process, sw, timeoutToken, attemptToken);
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
            await HandleFailureAsync(cmd, ex.Message, ex, sw);
        }
        finally
        {
            // Очищаем temp-файлы этой попытки
            commandPreparer.CleanupTempFiles(cmd.CommandId, attemptToken);

            // На shutdown не удаляем процесс из tracking'а — пусть LogActiveProcessesOnShutdownAsync его увидит.
            if (!ct.IsCancellationRequested)
            {
                _ = _activeProcesses.TryRemove(cmd.CommandId, out var removedProcess);
                removedProcess?.Dispose();
            }

        }
    }

    /// <summary>Запускает процесс по конфигурации команды.</summary>
    private async Task<Process> StartProcessAsync(PendingCommand cmd, CommandConfig commandCfg, string attemptToken)
    {
        // Создаём task-файл для CAD-плагина перед запуском процесса.
        // Если запись не удалась — AddIn не получит filePath (контракт BimPluginContract §CLI Arguments
        // запрещает передачу .rvt-пути в CLI args), и команда гарантированно упадёт. Fail-fast
        // с IOException, чтобы ErrorClassifier пометил это как permanent failure без retry:
        // проблема инфраструктурная (TaskDirectory недоступен/переполнен/заблокирован антивирусом),
        // повторная попытка ничего не даст.
        var (_, taskFilePath) = commandPreparer.GetTaskFilePaths(cmd.CommandId, attemptToken);
        if (!commandPreparer.CreateTaskFile(cmd, attemptToken))
        {
            throw new IOException(
                $"Failed to write task file in TaskDirectory '{taskFilePath}'. " +
                $"AddIn cannot proceed without the task file. Check FileSystem:TaskDirectory permissions, disk space, and antivirus.");
        }

        var startInfo = commandPreparer.CreateProcessStartInfo(cmd, commandCfg, attemptToken);
        var (resultFilePath, _) = commandPreparer.GetTaskFilePaths(cmd.CommandId, attemptToken);

        logger.LogInformation("Command start: id={Id}, correlationId={CorrelationId}, command={Cmd}, attempt={Attempt}",
            cmd.CommandId, cmd.CorrelationId, cmd.CommandText, cmd.RetryCount + 1);
        logger.LogInformation(
            "External process start: id={Id}, correlationId={CorrelationId}, exe={ExecutablePath}, args={Arguments}, workingDirectory={WorkingDirectory}, taskFile={TaskFilePath}, expectedResultFile={ResultFilePath}",
            cmd.CommandId, cmd.CorrelationId, startInfo.FileName, startInfo.Arguments, startInfo.WorkingDirectory, taskFilePath, resultFilePath);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            _ = process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Process.Start() бросает Win32Exception при отсутствии exe / access denied,
            // InvalidOperationException — если процесс уже стартовал или не задан FileName.
            // Disposed, чтобы Process не утекал: outer finally в RunAsync() увидит process == null
            // и ничего не dispose'нет — поэтому освобождаем здесь.
            process.Dispose();
            throw;
        }
        _activeProcesses[cmd.CommandId] = process;
        _ = await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Processing, process.Id);
        _ = commandDataService.NotifySessionStartedAsync(cmd.SessionId, cmd.CorrelationId, cmd.UserId);

        return process;
    }

    /// <summary>Ожидает завершения процесса, собирает stdout/stderr с ограничением размера.
    /// Использует потоковую обработку для предотвращения переполнения памяти.
    /// После выхода процесса пробует прочитать result-файл от плагина.
    /// Если файл есть — статус берётся из него. Если нет — fallback на exit code.
    /// </summary>
    private async Task WaitAndHandleResultAsync(PendingCommand cmd, Process process, Stopwatch sw, CancellationToken ct, string attemptToken)
    {
        const int MaxOutputChars = 64 * 1024; // 64KB лимит на вывод
        var outputBuilder = new StringBuilder(capacity: 1024);
        var errorBuilder = new StringBuilder(capacity: 1024);
        var outputTruncated = false;
        var errorTruncated = false;

        // Потоковая обработка stdout/stderr с ограничением размера
        void OnOutputDataReceived(object? sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                AppendBounded(outputBuilder, e.Data, ref outputTruncated, MaxOutputChars);
            }
        }

        void OnErrorDataReceived(object? sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                AppendBounded(errorBuilder, e.Data, ref errorTruncated, MaxOutputChars);
            }
        }

        process.OutputDataReceived += OnOutputDataReceived;
        process.ErrorDataReceived += OnErrorDataReceived;

        try
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(ct);

            // Дожидаемся завершения async-обработчиков stdout/stderr после выхода процесса.
            process.WaitForExit();
        }
        finally
        {
            process.OutputDataReceived -= OnOutputDataReceived;
            process.ErrorDataReceived -= OnErrorDataReceived;
        }

        LogProcessOutput(cmd, outputBuilder, errorBuilder, outputTruncated, errorTruncated);
        sw.Stop();

        // Пробуем прочитать result-файл от плагина
        var resultReadStatus = TryReadResultFile(cmd.CommandId, attemptToken, out var result, out var resultReadError);
        if (resultReadStatus == ResultFileReadStatus.NotFound)
        {
            var (expectedResultFilePath, _) = commandPreparer.GetTaskFilePaths(cmd.CommandId, attemptToken);
            logger.LogInformation(
                "Result file not found: id={Id}, correlationId={CorrelationId}, command={Cmd}, expectedResultFile={ResultFilePath}. Falling back to process exit code.",
                cmd.CommandId, cmd.CorrelationId, cmd.CommandText, expectedResultFilePath);
        }

        if (resultReadStatus == ResultFileReadStatus.Valid)
        {
            if (result.Status == ResultStatus.Done)
            {
                _ = await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Done);
                logger.LogInformation(
                    "Command done (plugin): id={Id}, correlationId={CorrelationId}, command={Cmd}, status={Status}, outputPath={OutputPath}, elapsedMs={ElapsedMs}",
                    cmd.CommandId, cmd.CorrelationId, cmd.CommandText, result.Status, result.OutputFiles ?? "<none>", sw.ElapsedMilliseconds);
                await sessionCompletionTracker.OnCommandCompletedAsync(cmd);
                return;
            }

            // Failed или Cancelled. Cancelled — permanent failure (без retry), как и Failed через ErrorClassifier.
            if (result.Status == ResultStatus.Cancelled)
            {
                var cancellationMessage = result.ErrorMessage ?? "Plugin reported cancellation";
                logger.LogInformation(
                    "Command cancelled by plugin: id={Id}, correlationId={CorrelationId}, command={Cmd}, elapsedMs={ElapsedMs}, error={Error}",
                    cmd.CommandId, cmd.CorrelationId, cmd.CommandText, sw.ElapsedMilliseconds, cancellationMessage);

                _ = await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Failed, errorMessage: cancellationMessage);
                await sessionCompletionTracker.OnCommandCompletedAsync(cmd);
                return;
            }
            else
            {
                logger.LogWarning(
                    "Command plugin failure: id={Id}, correlationId={CorrelationId}, command={Cmd}, status={Status}, elapsedMs={ElapsedMs}, error={Error}",
                    cmd.CommandId, cmd.CorrelationId, cmd.CommandText, result.Status, sw.ElapsedMilliseconds, result.ErrorMessage ?? "Plugin reported failure");
            }

            // Логируем stack trace при наличии (errorDetails — поле для диагностики, см. BimPluginContract).
            if (!string.IsNullOrWhiteSpace(result.ErrorDetails))
            {
                logger.LogDebug("Plugin errorDetails for id={Id}: {Details}", cmd.CommandId, result.ErrorDetails);
            }

            await HandleFailureAsync(cmd, result.ErrorMessage ?? "Plugin reported failure", null, sw);
            return;
        }

        if (resultReadStatus == ResultFileReadStatus.Invalid)
        {
            await HandleFailureAsync(cmd, resultReadError ?? "Invalid plugin result file", null, sw);
            return;
        }

        // Fallback: exit code (для команд без плагина, который пишет result-файл)
        if (process.ExitCode == 0)
        {
            // Процесс завершился с кодом 0, но result-файл не был найден и не распарсен.
            // Это типичный симптом нарушения контракта BimPlugin со стороны AddIn: он
            // получил CLI args, но TaskFilePathResolver не нашёл task-файл по строгому layout:
            // args[1] == /command, args[2] == WORKER, args[3] == task-файл. Например,
            // если Worker передал commandText вместо WORKER, AddIn считает argv malformed и
            // вернул Result.Cancelled без записи ResultFile. Логируем громко с
            // подсказкой — это ускоряет диагностику, когда плагин «молча» падает.
            logger.LogWarning(
                "Process exited cleanly (exitCode=0) but no result file was written for command {Id} (correlationId={CorrelationId}, command={Cmd}, elapsedMs={ElapsedMs}). " +
                "Possible causes: AddIn's TaskFilePathResolver did not match the CLI args layout, or AddIn wrote ResultFile to a different path. " +
                "Verify the AddIn version matches the ArgumentsTemplate in appsettings.json.",
                cmd.CommandId, cmd.CorrelationId, cmd.CommandText, sw.ElapsedMilliseconds);

            _ = await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Done);
            logger.LogInformation("Command done: id={Id}, correlationId={CorrelationId}, command={Cmd}, elapsedMs={ElapsedMs}",
                cmd.CommandId, cmd.CorrelationId, cmd.CommandText, sw.ElapsedMilliseconds);
            await sessionCompletionTracker.OnCommandCompletedAsync(cmd);
        }
        else
        {
            var errorMessage = $"Process exited with code {process.ExitCode}";
            logger.LogWarning("Command exit: id={Id}, correlationId={CorrelationId}, command={Cmd}, exitCode={ExitCode}, elapsedMs={ElapsedMs}",
                cmd.CommandId, cmd.CorrelationId, cmd.CommandText, FormatExitCode(process.ExitCode), sw.ElapsedMilliseconds);
            await HandleFailureAsync(cmd, errorMessage, null, sw, process.ExitCode);
        }
    }

    /// <summary>
    /// Пробует прочитать result-файл из TaskDirectory (настраивается через <c>FileSystem:TaskDirectory</c>).
    /// Возвращает true, если файл существует и успешно распарсен.
    /// Порядок: сначала парсим, потом удаляем — чтобы при битом JSON
    /// файл остался для диагностики.
    /// </summary>
    private ResultFileReadStatus TryReadResultFile(
        int commandId,
        string attemptToken,
        out ResultFile result,
        out string? errorMessage)
    {
        var (path, _) = commandPreparer.GetTaskFilePaths(commandId, attemptToken);

        if (!File.Exists(path))
        {
            result = null!;
            errorMessage = null;
            return ResultFileReadStatus.NotFound;
        }

        try
        {
            var json = File.ReadAllText(path);

            result = JsonSerializer.Deserialize<ResultFile>(json, JsonOptions.CamelCase)!;

            // Status — enum (Done/Failed/Cancelled). required поле → если десериализация прошла, status всегда валиден.
            if (Enum.IsDefined(result.Status))
            {
                // Удаляем ТОЛЬКО после успешного парсинга
                File.Delete(path);
                errorMessage = null;
                return ResultFileReadStatus.Valid;
            }

            // Невалидный status — rename для диагностики
            RenameToBadFile(path);
            result = null!;
            errorMessage = $"Plugin result file has invalid status: {path}";
            return ResultFileReadStatus.Invalid;
        }
        catch (JsonException ex)
        {
            // Битый JSON — rename для диагностики
            RenameToBadFile(path);
            result = null!;
            errorMessage = $"Plugin result file contains invalid JSON: {path}. {ex.Message}";
            return ResultFileReadStatus.Invalid;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result = null!;
            errorMessage = $"Plugin result file cannot be read: {path}. {ex.Message}";
            return ResultFileReadStatus.Invalid;
        }
    }

    /// <summary>Переименовывает битый result-файл в .bad, чтобы не парсить его повторно, но оставить для диагностики.</summary>
    private void RenameToBadFile(string path)
    {
        try
        {
            File.Move(path, path + ".bad", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to rename invalid result file to .bad: {Path}", path);
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
            await ProcessKillHelper.KillAsync(process, TimeSpan.FromSeconds(PerProcessKillTimeoutSeconds), logger, cmd.CommandId);
        }

        logger.LogError("Command {CommandId} ({Cmd}) timed out: correlationId={CorrelationId}, timeout={Timeout} min, elapsed={Elapsed:F1}s",
            cmd.CommandId, cmd.CommandText, cmd.CorrelationId, _workerOptions.ProcessTimeoutMinutes, sw.Elapsed.TotalSeconds);

        _ = await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Failed,
            errorMessage: $"Process timed out after {_workerOptions.ProcessTimeoutMinutes} min");
        await sessionCompletionTracker.OnCommandCompletedAsync(cmd);
    }

    /// <summary>
    /// Планирует retry (для ProcessCrashError) или помечает команду как Failed (для InvalidFileError).
    /// Использует <see cref="ErrorClassifier"/> для классификации ошибки.
    /// </summary>
    private async Task HandleFailureAsync(PendingCommand cmd, string errorMessage, Exception? ex, Stopwatch sw, int? exitCode = null)
    {
        sw.Stop();

        // Определяем тип ошибки: permanent (InvalidFileError — повторять бессмысленно)
        // vs transient (ProcessCrashError — можно повторить)
        var isPermanent = ErrorClassifier.IsPermanentFailure(errorMessage, exitCode, _workerOptions.PermanentFailureExitCodes)
                          || ErrorClassifier.IsPermanentException(ex);

        if (isPermanent)
        {
            // InvalidFileError → сразу Failed, без retry
            _ = await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Failed, errorMessage: errorMessage);
            logger.LogError(ex, "Command {Cmd} ({Id}) failed with permanent error (no retry): correlationId={CorrelationId}, exitCode={ExitCode}, elapsedMs={ElapsedMs}, error={Msg}",
                cmd.CommandText, cmd.CommandId, cmd.CorrelationId, FormatExitCode(exitCode), sw.ElapsedMilliseconds, errorMessage);
            await sessionCompletionTracker.OnCommandCompletedAsync(cmd);
        }
        else if (cmd.RetryCount < _workerOptions.MaxRetries)
        {
            // ProcessCrashError → retry с экспоненциальной задержкой
            var nextRetryAt = DateTime.UtcNow.AddSeconds(
                _workerOptions.RetryDelayBaseSeconds * (1 << cmd.RetryCount));
            var newRetryCount = await commandDataService.ScheduleRetryAsync(
                cmd.CommandId, nextRetryAt, errorMessage);
            logger.LogWarning(ex, "Command {Cmd} ({Id}) failed: correlationId={CorrelationId}, attempt={Attempt}/{Max}, retryAt={Next:O}, exitCode={ExitCode}, elapsedMs={ElapsedMs}, error={Msg}",
                cmd.CommandText, cmd.CommandId, cmd.CorrelationId, newRetryCount, _workerOptions.MaxRetries,
                nextRetryAt, FormatExitCode(exitCode), sw.ElapsedMilliseconds, errorMessage);
            await sessionCompletionTracker.OnCommandCompletedAsync(cmd);
        }
        else
        {
            // Исчерпаны все retry → Failed
            _ = await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Failed, errorMessage: errorMessage);
            logger.LogError(ex, "Command {Cmd} ({Id}) failed after attempts: correlationId={CorrelationId}, attempt={Attempt}, exitCode={ExitCode}, elapsedMs={ElapsedMs}, error={Msg}",
                cmd.CommandText, cmd.CommandId, cmd.CorrelationId, cmd.RetryCount + 1, FormatExitCode(exitCode), sw.ElapsedMilliseconds, errorMessage);
            await sessionCompletionTracker.OnCommandCompletedAsync(cmd);
        }
    }

    /// <summary>Логирует stdout и stderr процесса с информацией о truncation.</summary>
    private void LogProcessOutput(PendingCommand cmd, StringBuilder outputBuilder, StringBuilder errorBuilder, bool outputTruncated, bool errorTruncated)
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

    /// <summary>Дописывает данные в builder с ограничением размера; выставляет truncated при превышении лимита.</summary>
    private static void AppendBounded(StringBuilder builder, string data, ref bool truncated, int maxChars)
    {
        if (truncated)
        {
            return;
        }

        lock (builder)
        {
            if (truncated)
            {
                return;
            }

            if (builder.Length + data.Length + 1 <= maxChars)
            {
                _ = builder.AppendLine(data);
                return;
            }

            // Дописываем сколько влезает и ставим флаг truncation
            var remaining = maxChars - builder.Length;
            if (remaining > 0)
            {
                _ = builder.Append(data.AsSpan(0, Math.Min(remaining, data.Length)));
            }

            truncated = true;
        }
    }

    /// <summary>Обрезает вывод до 4 КБ для предотвращения раздувания логов.</summary>
    private static string TruncateOutput(StringBuilder builder)
    {
        const int maxLength = 4096;
        return builder.Length > maxLength
            ? builder.ToString(0, maxLength) + $"\n... (truncated for log, total {builder.Length} chars)"
            : builder.ToString(0, builder.Length);
    }

    /// <summary>Превращает голый exit code в читаемый вид: decimal + hex + имя известного NTSTATUS-краша.</summary>
    private static string FormatExitCode(int? exitCode)
    {
        if (exitCode is not { } code)
        {
            return "n/a";
        }

        var name = code switch
        {
            unchecked((int)0xC0000005) => "ACCESS_VIOLATION",
            unchecked((int)0xC00000FD) => "STACK_OVERFLOW",
            unchecked((int)0xC0000135) => "DLL_NOT_FOUND",
            unchecked((int)0xC000013A) => "CONTROL_C_EXIT",
            unchecked((int)0xC0000409) => "STACK_BUFFER_OVERRUN",
            _ => null,
        };

        return name is null
            ? $"{code} (0x{code:X8})"
            : $"{code} (0x{code:X8}, {name})";
    }

    private enum ResultFileReadStatus
    {
        NotFound,
        Valid,
        Invalid,
    }
}
