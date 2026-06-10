using Microsoft.Extensions.Options;
using Npgsql;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;
using TelegramBot.Worker.BimLib.Interfaces;
using TelegramBot.Worker.BimLib.Models;
using TelegramBot.Worker.BimLib.Monitor;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Background service: событийная обработка очереди команд через PostgreSQL LISTEN/NOTIFY.
/// Слушает канал new_tasks и мгновенно реагирует на новые задачи.
/// Fallback-polling (настраивается через <c>Worker.FallbackPollingIntervalSeconds</c>)
/// используется только при потере соединения с уведомлением.
/// Автоматически переподключается при потере соединения.
/// </summary>
public sealed class CommandExecutionService(
    ISessionDataService sessionDataService,
    ICommandDataService commandDataService,
    INotificationDataService notificationDataService,
    IOptions<WorkerOptions> workerOptions,
    IConfiguration configuration,
    ILogger<CommandExecutionService> logger,
    IRevitVersionDetector versionDetector,
    INavisworksPathResolver navisworksPathResolver,
    DialogDismisser dialogDismisser) : BackgroundService
{
    private const string ListenChannel = "new_tasks";
    private const int DefaultBatchSize = 5;
    private const int ReconnectDelayMs = 5_000; // 5 сек между попытками переподключения

    private readonly string _connectionString = configuration.GetConnectionString("Postgres")
        ?? "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres";
    private readonly WorkerOptions _workerOptions = workerOptions.Value;

    // Партиции: максимальный Priority threshold -> пул процессов.
    // SortedDictionary гарантирует порядок по возрастанию threshold.
    private readonly SortedDictionary<int, SemaphoreSlim> _partitionPools = [];

    // Трекинг активных процессов для возможности принудительного завершения
    private readonly ConcurrentDictionary<int, Process> _activeProcesses = new();

    // Счётчик оставшихся команд по сессиям — избегает лишних SQL запросов
    // Устанавливается при ClaimPendingCommandsAsync, декрементится при завершении каждой команды
    private readonly ConcurrentDictionary<int, int> _sessionRemaining = new();

    // CancellationTokenSource для graceful shutdown
    private CancellationTokenSource? _shutdownCts;

    // Фоновая задача очистки истёкших lease — await'ится на shutdown
    private Task? _cleanupTask;

    // Фоновая задача мониторинга здоровья активных процессов — await'ится на shutdown
    private Task? _healthTask;

    // Закешированный массив threshold партиций (по возрастанию) для линейного lookup.
    private int[] _partitionThresholds = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        InitializePartitionPools();

        var partitionInfo = string.Join(", ", _partitionPools.Select(p => $"{p.Key}={p.Value.CurrentCount}"));

        logger.LogInformation("Worker starting: partitions={PartitionCount}, pools={Pools}", _partitionPools.Count, partitionInfo);

        _shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        _cleanupTask = StartCleanupTaskAsync();
        _healthTask = StartHealthMonitoringTaskAsync();

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
            await PerformGracefulShutdownAsync();
        }

        logger.LogInformation("Worker stopped");
    }

    /// <summary>
    /// Основной цикл обработки: LISTEN канала new_tasks + fallback polling.
    /// При получении уведомления мгновенно обрабатывает пакет задач.
    /// Fallback polling срабатывает только по истечении <c>FallbackPollingIntervalSeconds</c>.
    /// </summary>
    private async Task RunListenerLoopAsync(CancellationToken stoppingToken)
    {
        // Освобождаем истёкшие Lease и таймауты (crash recovery упавших воркеров)
        await commandDataService.ReleaseExpiredLeasesAsync();
        await commandDataService.ReleaseTimeoutCommandsAsync(_workerOptions.ProcessTimeoutSeconds);

        // Первичная проверка — вдруг команды уже есть в БД
        await ProcessBatchAsync(stoppingToken);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(stoppingToken);

        // Подписываемся на канал уведомлений
        await using var cmd = new NpgsqlCommand($"LISTEN {ListenChannel};", conn);
        _=await cmd.ExecuteNonQueryAsync(stoppingToken);

        logger.LogInformation("Listening for notifications on channel '{Channel}'", ListenChannel);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Ждём уведомление с таймаутом fallback polling
            var fallbackTimeoutSec = _workerOptions.FallbackPollingIntervalSeconds;
            var notificationReceived = await WaitForNotificationAsync(conn, TimeSpan.FromSeconds(fallbackTimeoutSec), stoppingToken);

            if (notificationReceived)
            {
                logger.LogDebug("Notification received on channel '{Channel}'", ListenChannel);
            }
            else
            {
                logger.LogDebug("Fallback polling triggered after {TimeoutSec}s", fallbackTimeoutSec);
            }

            // Обрабатываем доступные команды
            await ProcessBatchAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Ждёт уведомление PostgreSQL с указанным таймаутом.
    /// Возвращает true, если уведомление получено, false — по таймауту.
    /// </summary>
    private async Task<bool> WaitForNotificationAsync(NpgsqlConnection conn, TimeSpan timeout, CancellationToken ct)
    {
        var notificationReceived = false;
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        void OnNotification(object? sender, NpgsqlNotificationEventArgs e)
        {
            if (e.Channel == ListenChannel)
            {
                notificationReceived = true;
            }
        }

        conn.Notification += OnNotification;

        try
        {
            await conn.WaitAsync(timeoutCts.Token);
            return notificationReceived;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Таймаут истёк — это нормальная ситуация для fallback polling
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Отмена запроса — пробрасываем дальше
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error waiting for notification");
            return false;
        }
        finally
        {
            conn.Notification -= OnNotification;
            timeoutCts.Dispose();
        }
    }

    private Task StartCleanupTaskAsync()
    {
        return Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_workerOptions.CleanupIntervalSeconds));

            while (await timer.WaitForNextTickAsync(_shutdownCts!.Token))
            {
                try
                {
                    await commandDataService.ReleaseExpiredLeasesAsync();
                    await commandDataService.ReleaseTimeoutCommandsAsync(_workerOptions.ProcessTimeoutSeconds);
                    await CleanupInactiveSessionsAsync();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in lease cleanup cycle");
                }
            }
        });
    }

    private Task StartHealthMonitoringTaskAsync()
    {
        return Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_workerOptions.HealthCheckIntervalSeconds));

            while (await timer.WaitForNextTickAsync(_shutdownCts!.Token))
            {
                try
                {
                    CheckProcessesHealth();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in process health check cycle");
                }
            }
        });
    }

    private async Task PerformGracefulShutdownAsync()
    {
        logger.LogInformation("Worker stopping: activeProcesses={Count}", _activeProcesses.Count);
        await LogActiveProcessesOnShutdownAsync();

        if (_shutdownCts != null) await _shutdownCts.CancelAsync();

#pragma warning disable VSTHRD003
        await WaitForBackgroundTaskCompletionAsync(_cleanupTask, "Cleanup task");
        await WaitForBackgroundTaskCompletionAsync(_healthTask, "Health monitoring task");
#pragma warning restore VSTHRD003

        _shutdownCts?.Dispose();

        foreach (var pool in _partitionPools.Values)
        {
            pool.Dispose();
        }
    }

    private async Task WaitForBackgroundTaskCompletionAsync(Task? task, string taskName)
    {
        if (task == null)
        {
            return;
        }

        var timeout = Task.Delay(TimeSpan.FromSeconds(15), CancellationToken.None);
#pragma warning disable VSTHRD003
        if (await Task.WhenAny(task, timeout) != task)
#pragma warning restore VSTHRD003
        {
            logger.LogWarning("{TaskName} did not complete within 15s timeout", taskName);
        }
    }

    private async Task CleanupInactiveSessionsAsync()
    {
        if (_workerOptions.CompletedSessionRetentionDays <= 0)
        {
            return;
        }

        var cutoffUtc = DateTime.UtcNow.AddDays(-_workerOptions.CompletedSessionRetentionDays);
        var deletedCount = await sessionDataService.SoftDeleteInactiveSessionsOlderThanAsync(cutoffUtc);
        if (deletedCount > 0)
        {
            logger.LogInformation(
                "Inactive session cleanup completed: deleted={Count}, retentionDays={RetentionDays}",
                deletedCount, _workerOptions.CompletedSessionRetentionDays);
        }
    }

    /// <summary>
    /// Инициализирует per-partition пулы из конфигурации.
    /// Ключ словаря — максимальный Priority threshold.
    /// Команда попадает в первый threshold >= Priority. Меньшее Priority важнее.
    /// </summary>
    private void InitializePartitionPools()
    {
        foreach (var (threshold, poolSize) in _workerOptions.Partitions)
        {
            _partitionPools[threshold] = new SemaphoreSlim(poolSize, poolSize);
        }

        // Гарантируем, что хотя бы один пул существует
        if (_partitionPools.Count == 0)
        {
            _partitionPools[0] = new SemaphoreSlim(5, 5);
        }

        // Кешируем thresholds по возрастанию для выбора первого threshold >= Priority.
        _partitionThresholds = _partitionPools.Keys.ToArray();
    }

    /// <summary>
    /// Проверяет здоровье всех активных процессов: отклик, диалоги, память.
    /// </summary>
    private void CheckProcessesHealth()
    {
        foreach (var (commandId, process) in _activeProcesses)
        {
            if (process.HasExited)
            {
                continue;
            }

            try
            {
                var health = ProcessHealthHelper.CheckHealth(process, logger, $"Command#{commandId}");

                if (health.Status == RevitProcessStatus.NotResponding)
                {
                    logger.LogWarning(
                        "Process not responding: commandId={Id}, pid={Pid}, memoryMb={MemoryMb}, duration={Duration}",
                        commandId, process.Id, health.MemoryMb, health.Duration);
                }
                else if (health.Status == RevitProcessStatus.Healthy)
                {
                    logger.LogDebug(
                        "Process healthy: commandId={Id}, pid={Pid}, memoryMb={MemoryMb}, duration={Duration}",
                        commandId, process.Id, health.MemoryMb, health.Duration);
                }

                // Закрываем модальные диалоги Revit, если вылезли
                try
                {
                    _=dialogDismisser.DismissDialogsForProcess((uint)process.Id);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to dismiss dialogs for command {CommandId}", commandId);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Health check failed for command {CommandId}", commandId);
            }
        }
    }

    /// <summary>
    /// Логирует активные процессы при остановке Worker.
    /// Процессы не завершаются принудительно — Revit/Navisworks могут выполнять
    /// важную работу, и их прерывание может привести к повреждению данных.
    /// Worker просто останавливается, а процессы продолжают работу.
    /// Команды таких процессов будут подхвачены при следующем запуске через Crash Recovery.
    /// </summary>
    private async Task LogActiveProcessesOnShutdownAsync()
    {
        if (_activeProcesses.Count == 0)
        {
            logger.LogInformation("Worker shutdown: no active processes to handle");
            return;
        }

        logger.LogInformation("Worker shutdown: waiting up to 30s for {Count} active processes",
            _activeProcesses.Count);

        // Даём процессам шанс завершиться самостоятельно
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && _activeProcesses.Values.Any(p => !p.HasExited))
        {
            await Task.Delay(500);
        }

        var stillRunning = _activeProcesses.Values.Count(p => !p.HasExited);
        if (stillRunning > 0)
        {
            logger.LogInformation("Worker shutdown: {Count} processes still running after 30s, leaving them",
                stillRunning);
        }

        foreach (var kvp in _activeProcesses)
        {
            var process = kvp.Value;
            if (process.HasExited)
            {
                continue;
            }

            logger.LogInformation("Process left running: commandId={Id}, pid={Pid}", kvp.Key, process.Id);
        }
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        try
        {
            var leaseTimeoutMinutes = (_workerOptions.ProcessTimeoutSeconds + 300) / 60;
            var claimed = await commandDataService.ClaimPendingCommandsAsync(DefaultBatchSize, leaseTimeoutMinutes);

            if (claimed.Count == 0)
            {
                return;
            }

            logger.LogInformation("Worker batch claimed: count={Count}", claimed.Count);

            // Устанавливаем счётчик оставшихся команд по сессиям
            foreach (var group in claimed.GroupBy(c => c.SessionId))
            {
                _=_sessionRemaining.AddOrUpdate(group.Key, group.Count(), (_, existing) => existing + group.Count());
            }

            // Запускаем все команды параллельно, каждая ждёт свободный слот своей партиции
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
        var threshold = GetPartitionThreshold(cmd.Priority);
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
            _=pool.Release();
        }
    }

    private int GetPartitionThreshold(int priority)
    {
        foreach (var threshold in _partitionThresholds)
        {
            if (priority <= threshold)
            {
                return threshold;
            }
        }

        return _partitionThresholds[^1];
    }

    private async Task ExecuteOneAsync(PendingCommand cmd, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        Process? process = null;

        try
        {
            var commandCfg = await PrepareCommandAsync(cmd, ct);

            if (commandCfg == null)
            {
                return;
            }

            process = await StartCommandProcessAsync(cmd, commandCfg, ct);
            await WaitAndHandleProcessResultAsync(cmd, process, sw, ct);
        }
        catch (OperationCanceledException)
        {
            if (process == null || process.HasExited)
            {
                throw;
            }
        }
        catch (Exception ex)
        {
            await HandleCommandFailureAsync(cmd, ex.Message, ex, sw);
        }
        finally
        {
            _=_activeProcesses.TryRemove(cmd.CommandId, out _);
            logger.LogDebug("Completed: id={Id}, command={Cmd}, elapsedMs={ElapsedMs}", cmd.CommandId, cmd.CommandText, sw.ElapsedMilliseconds);
        }
    }

    private async Task<CommandConfig?> PrepareCommandAsync(PendingCommand cmd, CancellationToken ct)
    {
        if (!_workerOptions.Commands.TryGetValue(cmd.CommandText, out var commandCfg))
        {
            logger.LogWarning("Command failed: id={Id}, command={Cmd}, reason=unknown_command", cmd.CommandId, cmd.CommandText);
            _=await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Failed,
                errorMessage: $"Unknown command type: {cmd.CommandText}");
            await CompleteClaimedCommandAsync(cmd);
            return null;
        }

        if (!ValidateFilePath(cmd, commandCfg))
        {
            logger.LogWarning("Command failed: id={Id}, command={Cmd}, reason=invalid_file", cmd.CommandId, cmd.CommandText);
            _=await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Failed,
                errorMessage: $"File validation failed for path: {cmd.FilePath}");
            await CompleteClaimedCommandAsync(cmd);
            return null;
        }

        var (resolvedPath, resolutionError) = await ResolveExecutablePathAsync(cmd, commandCfg.ExecutablePath, cmd.CommandText, ct);
        if (resolvedPath == null)
        {
            logger.LogWarning("Command failed: id={Id}, command={Cmd}, reason=executable_not_found, error={Error}",
                cmd.CommandId, cmd.CommandText, resolutionError);
            _=await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Failed,
                errorMessage: resolutionError);
            await CompleteClaimedCommandAsync(cmd);
            return null;
        }

        commandCfg.ExecutablePath = resolvedPath;
        return commandCfg;
    }

    private async Task<Process> StartCommandProcessAsync(PendingCommand cmd, CommandConfig commandCfg, CancellationToken ct)
    {
        var startInfo = CreateProcessStartInfo(cmd, commandCfg);
        startInfo.FileName = commandCfg.ExecutablePath;

        logger.LogInformation("Command start: id={Id}, command={Cmd}, attempt={Attempt}",
            cmd.CommandId, cmd.CommandText, cmd.RetryCount + 1);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _=process.Start();
        _activeProcesses[cmd.CommandId] = process;
        _=await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Processing, process.Id);

        return process;
    }

    private async Task WaitAndHandleProcessResultAsync(PendingCommand cmd, Process process, Stopwatch sw, CancellationToken ct)
    {
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data != null) { outputBuilder.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) { errorBuilder.AppendLine(e.Data); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timeoutMs = _workerOptions.ProcessTimeoutSeconds * 1000;
        using var processTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        processTimeoutCts.CancelAfter(timeoutMs);

        bool completed;
        try
        {
#pragma warning disable VSTHRD003
            await process.WaitForExitAsync(processTimeoutCts.Token);
#pragma warning restore VSTHRD003
            completed = true;
        }
        catch (OperationCanceledException) when (processTimeoutCts.IsCancellationRequested)
        {
            completed = false;
        }

        LogProcessOutput(cmd, outputBuilder, errorBuilder);

        var errorMessage = await GetProcessErrorMessageAsync(cmd, process, completed, sw);

        sw.Stop();

        if (completed && process.ExitCode == 0)
        {
            _=await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Done);
            logger.LogInformation("Command done: id={Id}, command={Cmd}, elapsedMs={ElapsedMs}",
                cmd.CommandId, cmd.CommandText, sw.ElapsedMilliseconds);
            await CompleteClaimedCommandAsync(cmd);
        }
        else if (errorMessage != null)
        {
            await HandleCommandFailureAsync(cmd, errorMessage, null, sw);
        }
    }

    private async Task<string?> GetProcessErrorMessageAsync(PendingCommand cmd, Process process, bool completed, Stopwatch sw)
    {
        if (!completed)
        {
            // VSTHRD103: Kill(entireProcessTree: true) has no async equivalent in .NET
#pragma warning disable VSTHRD103
            process.Kill(true);
#pragma warning restore VSTHRD103
            try
            {
                using var killTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
#pragma warning disable VSTHRD003
                try { await process.WaitForExitAsync(killTimeout.Token); } catch (OperationCanceledException) { }
#pragma warning restore VSTHRD003
            }
            catch (Exception)
            {
                logger.LogWarning(
                    "Kill timeout after process timeout: commandId={Id}, pid={Pid}",
                    cmd.CommandId, process.Id);
            }

            logger.LogWarning("Command timeout: id={Id}, command={Cmd}, elapsedMs={ElapsedMs}",
                cmd.CommandId, cmd.CommandText, sw.ElapsedMilliseconds);
            return $"Timeout: process exceeded {_workerOptions.ProcessTimeoutSeconds}s limit";
        }

        if (process.ExitCode != 0)
        {
            logger.LogWarning("Command exit: id={Id}, command={Cmd}, exitCode={ExitCode}, elapsedMs={ElapsedMs}",
                cmd.CommandId, cmd.CommandText, process.ExitCode, sw.ElapsedMilliseconds);
            return $"Process exited with code {process.ExitCode}";
        }

        return null;
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

        if (cfg.AllowedExtensions == null || cfg.AllowedExtensions.Count == 0)
        {
            return true;
        }

        var ext = Path.GetExtension(cmd.FilePath)?.ToLowerInvariant();
        if (!cfg.AllowedExtensions.Contains(ext ?? ""))
        {
            logger.LogWarning("Validation failed: extension '{Ext}' not allowed for {Cmd} ({Id}). Allowed: {Allowed}",
                ext, cmd.CommandText, cmd.CommandId, string.Join(", ", cfg.AllowedExtensions));
            return false;
        }

        return true;
    }

    /// <summary>
    /// Пытается определить версию Revit/Navisworks через BimLib и вернуть полный путь к исполняемому файлу.
    /// Если определить не удалось или нужная версия не установлена — возвращает (null, сообщение об ошибке).
    /// <c>resolvedPath</c> не null только когда путь успешно определён.
    /// </summary>
    /// <remarks>
    /// <b>Revit:</b> версия определяется из содержимого файла (OLE-поток BasicFileInfo → "Format: YYYY"),
    /// затем путь резолвится через реестр Windows. Это гарантирует запуск правильной версии Revit.exe
    /// для каждого .rvt-файла.
    /// <br/>
    /// <b>Navisworks:</b> в отличие от Revit, файлы .nwc/.nwd/.nwf не хранят версию в OLE-структуре.
    /// BasicFileInfo отсутствует. Поэтому версия не детектится — вместо этого выбирается первая
    /// установленная версия Navisworks (любой версии FileConvert.exe подходит для конвертации NWC).
    /// </remarks>
    private async Task<(string? resolvedPath, string? errorMessage)> ResolveExecutablePathAsync(PendingCommand cmd, string configuredPath, string commandText, CancellationToken ct)
    {
        if (commandText is "PDF" or "DWG" or "IFC" or "BIMDOC")
        {
            return await ResolveRevitPathAsync(cmd, commandText, ct) ?? (configuredPath, null);
        }

        if (commandText is "NWC" or "CLASHREP")
        {
            return ResolveNavisworksPath(commandText) ?? (configuredPath, null);
        }

        return (configuredPath, null);
    }

    private async Task<(string? resolvedPath, string? errorMessage)?> ResolveRevitPathAsync(PendingCommand cmd, string commandText, CancellationToken ct)
    {
        try
        {
            var version = await versionDetector.DetectVersionAsync(cmd.FilePath!, ct);
            if (version?.ExecutablePath != null)
            {
                logger.LogDebug("Resolved {Cmd} executable via BimLib: {Path} (Revit {Year})", commandText, version.ExecutablePath, version.Year);
                return (version.ExecutablePath, null);
            }

            if (version != null && version.ExecutablePath == null)
            {
                var msg = $"Revit {version.Year} не установлен на сервере. Пожалуйста, установите Revit {version.Year} или обратитесь к администратору.";
                logger.LogWarning("Could not resolve {Cmd}: {Msg}", commandText, msg);
                return (null, msg);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "BimLib version detection failed for {Cmd}, falling back to configured path", commandText);
        }

        return null;
    }

    private (string? resolvedPath, string? errorMessage)? ResolveNavisworksPath(string commandText)
    {
        try
        {
            var versions = navisworksPathResolver.GetInstalledVersions();
            if (versions.Count == 0)
            {
                var msg = "Navisworks не установлен на сервере. Пожалуйста, установите Navisworks или обратитесь к администратору.";
                logger.LogWarning("Could not resolve {Cmd}: {Msg}", commandText, msg);
                return (null, msg);
            }

            var nwPath = navisworksPathResolver.ResolveFileConvertPath(versions[0])
                          ?? navisworksPathResolver.ResolveNavisworksPath(versions[0]);
            if (nwPath != null)
            {
                logger.LogDebug("Resolved {Cmd} executable via BimLib: {Path} (Navisworks {Year})",
                    commandText, nwPath, versions[0]);
                return (nwPath, null);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "BimLib Navisworks resolution failed for {Cmd}, falling back to configured path",
                commandText);
        }

        return null;
    }

    /// <summary>
    /// Планирует retry или помечает команду как Failed, уведомляет пользователя.
    /// </summary>
    private async Task HandleCommandFailureAsync(PendingCommand cmd, string errorMessage, Exception? ex, Stopwatch sw)
    {
        sw.Stop();
        if (cmd.RetryCount < _workerOptions.MaxRetries)
        {
            var nextRetryAt = DateTime.UtcNow.AddSeconds(
                _workerOptions.RetryDelayBaseSeconds * (1 << cmd.RetryCount));
            var newRetryCount = await commandDataService.ScheduleRetryAsync(
                cmd.CommandId, nextRetryAt, errorMessage);
            logger.LogWarning(ex, "Command {Cmd} ({Id}) failed (attempt {Attempt}/{Max}), retry at {Next}. Error: {Msg}",
                cmd.CommandText, cmd.CommandId, newRetryCount, _workerOptions.MaxRetries,
                nextRetryAt.ToString("O"), errorMessage);
            await CompleteClaimedCommandAsync(cmd);
        }
        else
        {
            _=await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Failed, errorMessage: errorMessage);
            logger.LogError(ex, "Command {Cmd} ({Id}) failed after {Attempt} attempts. Error: {Msg}",
                cmd.CommandText, cmd.CommandId, cmd.RetryCount + 1, errorMessage);
            await CompleteClaimedCommandAsync(cmd);
        }
    }

    /// <summary>
    /// Декрементирует in-memory счётчик сессии после выхода захваченной команды из processing.
    /// Если текущий batch по сессии закончился, БД остаётся источником истины: retry-команды
    /// снова pending, поэтому уведомление отправляется только когда pending/processing уже нет.
    /// </summary>
    private async Task CompleteClaimedCommandAsync(PendingCommand cmd)
    {
        // AddOrUpdate атомарен: каждая команда видит уникальное значение счетчика.
        // Только поток, получивший 0, проверяет БД на предмет окончания сессии.
        var newRemaining = _sessionRemaining.AddOrUpdate(
            cmd.SessionId,
            _ => 0, // fallback — не должен сработать, т.к. ключ уже есть
            (_, current) => current - 1);

        if (newRemaining != 0)
        {
            return;
        }

        _ = _sessionRemaining.TryRemove(cmd.SessionId, out _);

        try
        {
            // Проверяем БД: если ещё есть pending или processing команды — сессия не завершена.
            // Это корректно обрабатывает случай, когда команд в сессии > DefaultBatchSize.
            var remainingInDb = await sessionDataService.CountPendingProcessingBySessionAsync(cmd.SessionId);
            if (remainingInDb > 0)
            {
                logger.LogDebug("Session {SessionId}: counter zero but {Remaining} commands still pending/processing in DB, skipping notification",
                    cmd.SessionId, remainingInDb);
                return;
            }

            var status = await sessionDataService.GetSessionsStatusAsync(cmd.SessionId);
            await notificationDataService.NotifyCommandCompletedAsync(
                cmd.UserId, cmd.SessionId, status.DoneFiles, status.TotalFiles, status.ProjectName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to notify session completion: session={SessionId}", cmd.SessionId);
        }
    }

    /// <summary>Логирует stdout и stderr процесса (только если есть вывод).</summary>
    private void LogProcessOutput(PendingCommand cmd, StringBuilder outputBuilder, StringBuilder errorBuilder)
    {
        if (outputBuilder.Length > 0)
        {
            logger.LogInformation("Output [{Cmd} {Id}]: {Output}",
                cmd.CommandText, cmd.CommandId, TruncateOutput(outputBuilder));
        }

        if (errorBuilder.Length > 0)
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
            : builder.ToString(0, builder.Length);
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
