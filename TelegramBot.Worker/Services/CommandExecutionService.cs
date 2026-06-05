using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Background service: ожидает уведомления через Postgres LISTEN/NOTIFY,
/// при получении сигнала проверяет БД на наличие новых команд и выполняет их.
/// Реализует per-partition пул процессов с лимитами для защиты от перегрузки.
/// Автоматически переподключается при потере соединения.
/// </summary>
public sealed class CommandExecutionService(
    IDataService dataService,
    IConfiguration configuration,
    IOptions<WorkerOptions> workerOptions,
    ILogger<CommandExecutionService> logger) : BackgroundService
{
    private const int FallbackTimeoutSec = 300; // 5 мин — safety net, если NOTIFY потерян
    private const int DefaultBatchSize = 50;
    private const int ReconnectDelayMs = 5_000; // 5 сек между попытками переподключения
    private const int CleanupIntervalSec = 60; // Интервал очистки истёкших lease

    private readonly string _connectionString = configuration.GetConnectionString("Postgres")
        ?? "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres";

    private readonly WorkerOptions _workerOptions = workerOptions.Value;

    // Партиции: ID → пул процессов. SortedDictionary гарантирует порядок по возрастанию ID.
    private readonly SortedDictionary<int, SemaphoreSlim> _partitionPools = new();

    // Трекинг активных процессов для возможности принудительного завершения
    private readonly ConcurrentDictionary<int, Process> _activeProcesses = new();

    // CancellationTokenSource для graceful shutdown
    private CancellationTokenSource? _shutdownCts;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        InitializePartitionPools();

        logger.LogInformation("Worker starting: partitions={PartitionCount}, pools={Pools}",
            _partitionPools.Count,
            string.Join(", ", _partitionPools.Select(p => $"{p.Key}={p.Value.CurrentCount}")));

        _shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        // Запускаем фоновую задачу периодической очистки истёкших lease
        var cleanupTask = Task.Run(async () =>
        {
            while (!_shutdownCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(CleanupIntervalSec), _shutdownCts.Token);
                    await dataService.ReleaseExpiredLeasesAsync();
                    await dataService.ReleaseTimeoutCommandsAsync(_workerOptions.ProcessTimeoutSeconds);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in lease cleanup cycle");
                }
            }
        });

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunListenerLoopAsync(stoppingToken);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Worker listener lost: retryMs={Delay}", ReconnectDelayMs);
                    await Task.Delay(ReconnectDelayMs, stoppingToken);
                }
            }
        }
        finally
        {
            // Graceful shutdown: ждём завершения активных процессов
            logger.LogInformation("Worker stopping: activeProcesses={Count}", _activeProcesses.Count);
            await WaitForActiveProcessesAsync();
            _shutdownCts?.Dispose();

            foreach (var pool in _partitionPools.Values)
                pool.Dispose();
        }

        logger.LogInformation("Worker stopped");
    }

    /// <summary>
    /// Инициализирует per-partition пулы из конфигурации.
    /// Ключ словаря — минимальный порог приоритета (threshold).
    /// Команды с Priority >= threshold попадают в соответствующую партицию.
    /// </summary>
    private void InitializePartitionPools()
    {
        foreach (var (threshold, poolSize) in _workerOptions.Partitions)
        {
            _partitionPools[threshold] = new SemaphoreSlim(poolSize, poolSize);
        }

        // Гарантируем, что хотя бы один пул существует
        if (_partitionPools.Count == 0)
            _partitionPools[0] = new SemaphoreSlim(5, 5);
    }

    private async Task WaitForActiveProcessesAsync()
    {
        var timeout = TimeSpan.FromSeconds(30);
        var start = DateTime.UtcNow;

        while (_activeProcesses.Count > 0 && (DateTime.UtcNow - start) < timeout)
        {
            logger.LogDebug("Waiting for {Count} active processes to complete...", _activeProcesses.Count);
            await Task.Delay(500);
        }

        if (_activeProcesses.Count > 0)
        {
            logger.LogWarning("Forcing termination of {Count} active processes", _activeProcesses.Count);
            foreach (var process in _activeProcesses.Values)
            {
                try { process.Kill(true); }
                catch { }
            }
        }
    }

    private async Task RunListenerLoopAsync(CancellationToken stoppingToken)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(stoppingToken);

        _=await conn.ExecuteAsync("LISTEN new_command;");

        conn.Notification += OnNotificationReceived;

        logger.LogInformation("Worker listening: channel=new_command");

        // Освобождаем истёкшие Lease (crash recovery упавших воркеров)
        await dataService.ReleaseExpiredLeasesAsync();

        // Первичная проверка — вдруг команды уже есть в БД
        await ProcessBatchAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Блокирующее ожидание NOTIFY или таймаут (5 мин)
                // При NOTIFY — просыпается мгновенно
                // При таймауте — fallback poll (safety net)
                _=await conn.WaitAsync(TimeSpan.FromSeconds(FallbackTimeoutSec), stoppingToken);
            }
            catch (TimeoutException)
            {
                // Fallback poll — если NOTIFY был потерян
                logger.LogDebug("Worker poll: reason=timeout");
            }
            catch (NpgsqlException ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Worker listener error: source=postgres");
                // Выходим из цикла → outer reconnect
            }

            await ProcessBatchAsync(stoppingToken);
        }
    }

    private void OnNotificationReceived(object sender, NpgsqlNotificationEventArgs e)
    {
        logger.LogDebug("Worker notify: channel={Channel}, payload={Payload}",
            e.Channel, e.Payload);
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        try
        {
            var claimed = await dataService.ClaimPendingCommandsAsync(DefaultBatchSize, (_workerOptions.ProcessTimeoutSeconds + 300) / 60);

            if (claimed.Count == 0)
            {
                return;
            }

            logger.LogInformation("Worker batch claimed: count={Count}, ids={CommandIds}",
                claimed.Count, string.Join(",", claimed.Select(c => c.CommandId)));

            // Запускаем все команды параллельно, но каждая ждёт свободный слот своей партиции
            var tasks = claimed.Select(cmd => ProcessWithPoolAsync(cmd, ct));
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing batch");
        }
    }

    private async Task ProcessWithPoolAsync(PendingCommand cmd, CancellationToken ct)
    {
        // Определяем партицию по приоритету команды (ищем highest threshold <= cmd.Priority)
        // FirstOrDefault возвращает 0 (default int), если ни один threshold не подошёл.
        // Threshold 0 гарантированно существует в _partitionPools (см. InitializePartitionPools).
        var threshold = _partitionPools.Keys
            .Reverse()
            .FirstOrDefault(t => cmd.Priority >= t);

        var pool = _partitionPools[threshold];

        logger.LogDebug("Command partition: id={Id}, command={Cmd}, priority={Prio}, threshold={Threshold}, slots={Slots}",
            cmd.CommandId, cmd.CommandText, cmd.Priority, threshold, pool.CurrentCount);

        await pool.WaitAsync(ct);

        try
        {
            await ExecuteOneAsync(cmd, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error executing command {CommandId}", cmd.CommandId);
        }
        finally
        {
            pool.Release();
        }
    }

    private async Task ExecuteOneAsync(PendingCommand cmd, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        Process? process = null;

        try
        {
            if (!_workerOptions.Commands.TryGetValue(cmd.CommandText, out var commandCfg))
            {
                logger.LogWarning("Command failed: id={Id}, command={Cmd}, reason=unknown_command", cmd.CommandId, cmd.CommandText);
                _=await dataService.UpdateCommandStatusAsync(cmd.CommandId, CommandStatuses.Failed,
                    errorMessage: $"Unknown command type: {cmd.CommandText}");
                return;
            }

            if (!ValidateFilePath(cmd, commandCfg))
            {
                logger.LogWarning("Command failed: id={Id}, command={Cmd}, reason=invalid_file", cmd.CommandId, cmd.CommandText);
                _=await dataService.UpdateCommandStatusAsync(cmd.CommandId, CommandStatuses.Failed,
                    errorMessage: $"File validation failed for path: {cmd.FilePath}");
                return;
            }

            var startInfo = CreateProcessStartInfo(cmd, commandCfg);
            logger.LogInformation("Command start: id={Id}, command={Cmd}, attempt={Attempt}",
                cmd.CommandId, cmd.CommandText, cmd.RetryCount + 1);

            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _=process.Start();
            _activeProcesses[cmd.CommandId] = process;
            _=await dataService.UpdateCommandStatusAsync(cmd.CommandId, CommandStatuses.Processing, process.Id);

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (_, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var timeoutMs = _workerOptions.ProcessTimeoutSeconds * 1000;
            var completed = await Task.Run(() => process.WaitForExit(timeoutMs), ct);

            LogProcessOutput(cmd, outputBuilder, errorBuilder);

            var errorMessage = (string?)null;

            if (!completed)
            {
                process.Kill(true);
                await process.WaitForExitAsync(ct);
                errorMessage = $"Timeout: process exceeded {_workerOptions.ProcessTimeoutSeconds}s limit";
                logger.LogWarning("Command timeout: id={Id}, command={Cmd}, elapsedMs={ElapsedMs}",
                    cmd.CommandId, cmd.CommandText, sw.ElapsedMilliseconds);
            }
            else if (process.ExitCode != 0)
            {
                errorMessage = $"Process exited with code {process.ExitCode}";
                logger.LogWarning("Command exit: id={Id}, command={Cmd}, exitCode={ExitCode}, elapsedMs={ElapsedMs}",
                    cmd.CommandId, cmd.CommandText, process.ExitCode, sw.ElapsedMilliseconds);
            }

            sw.Stop();

            if (completed && process.ExitCode == 0)
            {
                _=await dataService.UpdateCommandStatusAsync(cmd.CommandId, CommandStatuses.Done);
                logger.LogInformation("Command done: id={Id}, command={Cmd}, elapsedMs={ElapsedMs}",
                    cmd.CommandId, cmd.CommandText, sw.ElapsedMilliseconds);
                await dataService.NotifyCommandCompletedAsync(cmd.UserId, cmd.CommandId, cmd.CommandText, CommandStatuses.Done, null);
            }
            else if (errorMessage != null && cmd.RetryCount < _workerOptions.MaxRetries)
            {
                var nextRetryAt = DateTime.UtcNow.AddSeconds(
                    _workerOptions.RetryDelayBaseSeconds * (1 << cmd.RetryCount));
                var newRetryCount = await dataService.ScheduleRetryAsync(
                    cmd.CommandId, nextRetryAt, errorMessage);
                logger.LogWarning("Command {Cmd} ({Id}) failed (attempt {Attempt}/{Max}), retry at {Next}. Error: {Msg}",
                    cmd.CommandText, cmd.CommandId, newRetryCount, _workerOptions.MaxRetries,
                    nextRetryAt.ToString("O"), errorMessage);
                // Будим воркер, чтобы он проверил очередь (команда подхватится после NextRetryAt)
                await dataService.NotifyNewCommandsAsync(cmd.SessionId);
            }
            else
            {
                _=await dataService.UpdateCommandStatusAsync(cmd.CommandId, CommandStatuses.Failed,
                    errorMessage: errorMessage ?? "Unknown error");
                logger.LogError("Command {Cmd} ({Id}) failed after {Attempt} attempts. Error: {Msg}",
                    cmd.CommandText, cmd.CommandId, cmd.RetryCount + 1, errorMessage);
                await dataService.NotifyCommandCompletedAsync(cmd.UserId, cmd.CommandId, cmd.CommandText, CommandStatuses.Failed, errorMessage);
            }
        }
        catch (OperationCanceledException)
        {
            if (process != null && !process.HasExited) process.Kill(true);
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            if (cmd.RetryCount < _workerOptions.MaxRetries)
            {
                var nextRetryAt = DateTime.UtcNow.AddSeconds(
                    _workerOptions.RetryDelayBaseSeconds * (1 << cmd.RetryCount));
                var newRetryCount = await dataService.ScheduleRetryAsync(
                    cmd.CommandId, nextRetryAt, ex.Message);
                logger.LogWarning(ex, "Command {Cmd} ({Id}) failed with exception (attempt {Attempt}/{Max}), retry at {Next}",
                    cmd.CommandText, cmd.CommandId, newRetryCount, _workerOptions.MaxRetries,
                    nextRetryAt.ToString("O"));
                // Будим воркер, чтобы он проверил очередь
                await dataService.NotifyNewCommandsAsync(cmd.SessionId);
            }
            else
            {
                _=await dataService.UpdateCommandStatusAsync(cmd.CommandId, CommandStatuses.Failed, errorMessage: ex.Message);
                logger.LogError(ex, "Command {Cmd} ({Id}) failed after {Attempt} attempts",
                    cmd.CommandText, cmd.CommandId, cmd.RetryCount + 1);
                await dataService.NotifyCommandCompletedAsync(cmd.UserId, cmd.CommandId, cmd.CommandText, CommandStatuses.Failed, ex.Message);
            }
        }
        finally
        {
            _=_activeProcesses.TryRemove(cmd.CommandId, out _);
        }
    }

    /// <summary>Валидация FilePath: существование файла, расширение, path traversal.</summary>
    private bool ValidateFilePath(PendingCommand cmd, CommandConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cmd.FilePath))
        {
            logger.LogWarning("Validation failed: empty file path for command {Cmd} ({Id})",
                cmd.CommandText, cmd.CommandId);
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(cmd.FilePath);
            if (fullPath != cmd.FilePath && !fullPath.Equals(cmd.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Validation failed: path traversal detected for '{File}' ({Id})",
                    cmd.FilePath, cmd.CommandId);
                return false;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Validation failed: invalid path '{File}' ({Id})",
                cmd.FilePath, cmd.CommandId);
            return false;
        }

        if (!File.Exists(cmd.FilePath))
        {
            logger.LogWarning("Validation failed: file not found '{File}' for {Cmd} ({Id})",
                cmd.FilePath, cmd.CommandText, cmd.CommandId);
            return false;
        }

        if (cfg.AllowedExtensions == null || cfg.AllowedExtensions.Count == 0) return true;

        var ext = Path.GetExtension(cmd.FilePath)?.ToLowerInvariant();
        if (!cfg.AllowedExtensions.Contains(ext ?? ""))
        {
            logger.LogWarning("Validation failed: extension '{Ext}' not allowed for {Cmd} ({Id}). Allowed: {Allowed}",
                ext, cmd.CommandText, cmd.CommandId, string.Join(", ", cfg.AllowedExtensions));
            return false;
        }

        return true;
    }

    /// <summary>Логирует stdout и stderr процесса.</summary>
    private void LogProcessOutput(PendingCommand cmd, StringBuilder outputBuilder, StringBuilder errorBuilder)
    {
        var output = outputBuilder.ToString();
        var error = errorBuilder.ToString();

        if (!string.IsNullOrWhiteSpace(output))
        {
            logger.LogInformation("Output [{Cmd} {Id}]: {Output}",
                cmd.CommandText, cmd.CommandId, TruncateOutput(outputBuilder));
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            logger.LogWarning("Stderr [{Cmd} {Id}]: {Error}",
                cmd.CommandText, cmd.CommandId, TruncateOutput(errorBuilder));
        }
    }

    /// <summary>Обрезает вывод процесса до 4 KB для предотвращения раздувания логов.</summary>
    private static string TruncateOutput(StringBuilder builder)
    {
        const int maxLength = 4096;
        return builder.Length > maxLength
            ? builder.ToString(0, maxLength) + $"\n... (truncated, total {builder.Length} chars)"
            : builder.ToString();
    }

    /// <summary>Создаёт ProcessStartInfo из конфигурации команды (ExecutablePath + ArgumentsTemplate).</summary>
    private static ProcessStartInfo CreateProcessStartInfo(PendingCommand cmd, CommandConfig cfg)
    {
        var args = cfg.ArgumentsTemplate
            .Replace("{CommandText}", cmd.CommandText)
            .Replace("{FilePath}", cmd.FilePath);

        var workingDir = cfg.WorkingDirectory switch
        {
            null or "" => Path.GetDirectoryName(cmd.FilePath),
            "." => Environment.CurrentDirectory,
            var dir => dir
        } ?? Environment.CurrentDirectory;

        return new ProcessStartInfo
        {
            FileName = cfg.ExecutablePath,
            Arguments = args,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
    }
}
