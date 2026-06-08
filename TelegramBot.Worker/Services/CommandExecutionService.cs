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
using TelegramBot.BimLib.Interfaces;
using TelegramBot.BimLib.Models;
using TelegramBot.BimLib.Monitor;
using TelegramBot.Data;

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
    ILogger<CommandExecutionService> logger,
    IRevitVersionDetector versionDetector,
    INavisworksPathResolver navisworksPathResolver,
    DialogDismisser dialogDismisser) : BackgroundService
{
    private const int FallbackTimeoutSec = 300; // 5 мин — safety net, если NOTIFY потерян
    private const int DefaultBatchSize = 50;
    private const int ReconnectDelayMs = 5_000; // 5 сек между попытками переподключения
    private const int CleanupIntervalSec = 60; // Интервал очистки истёкших lease
    private const int HealthCheckIntervalSec = 30; // Интервал проверки здоровья процессов

    private readonly string _connectionString = configuration.GetConnectionString("Postgres")
        ?? "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres";

    private readonly WorkerOptions _workerOptions = workerOptions.Value;

    // Партиции: ID → пул процессов. SortedDictionary гарантирует порядок по возрастанию ID.
    private readonly SortedDictionary<int, SemaphoreSlim> _partitionPools = [];

    // Трекинг активных процессов для возможности принудительного завершения
    private readonly ConcurrentDictionary<int, Process> _activeProcesses = new();

    // Per-command CancellationTokenSource для отмены команды пользователем
    // Ключ: CommandId, значение: CTS, который отменяется при получении NOTIFY command_cancel
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _commandCts = new();

    // CancellationTokenSource для graceful shutdown
    private CancellationTokenSource? _shutdownCts;

    // Фоновая задача очистки истёкших lease — await'ится на shutdown
    private Task? _cleanupTask;

    // Фоновая задача мониторинга здоровья активных процессов — await'ится на shutdown
    private Task? _healthTask;

    // Закешированный массив threshold партиций (по убыванию) для быстрого lookup
    private int[] _partitionThresholds = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        InitializePartitionPools();

        logger.LogInformation("Worker starting: partitions={PartitionCount}, pools={Pools}",
            _partitionPools.Count,
            string.Join(", ", _partitionPools.Select(p => $"{p.Key}={p.Value.CurrentCount}")));

        _shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        // Фоновая задача периодической очистки истёкших lease (через PeriodicTimer — без дрифта)
        _cleanupTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(CleanupIntervalSec));

            while (await timer.WaitForNextTickAsync(_shutdownCts.Token))
            {
                try
                {
                    await dataService.ReleaseExpiredLeasesAsync();
                    await dataService.ReleaseTimeoutCommandsAsync(_workerOptions.ProcessTimeoutSeconds);
                    await dataService.CleanupOldCancelledCommandsAsync(_workerOptions.CleanupOlderThanDays);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in lease cleanup cycle");
                }
            }
        });

        // Фоновая задача мониторинга здоровья активных процессов (каждые 30 сек)
        _healthTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(HealthCheckIntervalSec));

            while (await timer.WaitForNextTickAsync(_shutdownCts.Token))
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
            // Graceful shutdown
            logger.LogInformation("Worker stopping: activeProcesses={Count}", _activeProcesses.Count);
            await LogActiveProcessesOnShutdownAsync();

            // Отменяем фоновые задачи и ждём их завершения (макс 15 сек)
            _shutdownCts?.Cancel();

            if (_cleanupTask != null)
            {
                var timeout = Task.Delay(TimeSpan.FromSeconds(15), CancellationToken.None);
                if (await Task.WhenAny(_cleanupTask, timeout) != _cleanupTask)
                    logger.LogWarning("Cleanup task did not complete within 15s timeout");
            }

            if (_healthTask != null)
            {
                var timeout = Task.Delay(TimeSpan.FromSeconds(15), CancellationToken.None);
                if (await Task.WhenAny(_healthTask, timeout) != _healthTask)
                    logger.LogWarning("Health monitoring task did not complete within 15s timeout");
            }

            _shutdownCts?.Dispose();

            foreach (var pool in _partitionPools.Values)
            {
                pool.Dispose();
            }
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
        {
            _partitionPools[0] = new SemaphoreSlim(5, 5);
        }

        // Кешируем thresholds по убыванию для быстрого Array.Find в ProcessWithPoolAsync
        _partitionThresholds = _partitionPools.Keys.Reverse().ToArray();
    }

    /// <summary>
    /// Проверяет здоровье всех активных процессов: отклик, диалоги, память.
    /// </summary>
    private void CheckProcessesHealth()
    {
        foreach (var (commandId, process) in _activeProcesses)
        {
            if (process.HasExited)
                continue;

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
                    dialogDismisser.DismissDialogsForProcess((uint)process.Id);
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
    /// Worker просто отключается от LISTEN/NOTIFY, а процессы продолжают работу.
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
                continue;

            logger.LogInformation("Process left running: commandId={Id}, pid={Pid}",
                kvp.Key, process.Id);
        }
    }

    private async Task RunListenerLoopAsync(CancellationToken stoppingToken)
    {
        await using var conn = await NpgsqlHelper.CreateOpenConnectionAsync(_connectionString, stoppingToken);

        await conn.ExecuteAsync("LISTEN new_command;");
        await conn.ExecuteAsync("LISTEN command_cancel;");

        conn.Notification += OnNotificationReceived;

        logger.LogInformation("Worker listening: channels=new_command, command_cancel");

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
                await conn.WaitAsync(TimeSpan.FromSeconds(FallbackTimeoutSec), stoppingToken);
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
        if (e.Channel == "command_cancel")
        {
            _ = HandleCancelNotificationAsync(e.Payload);
        }
        else
        {
            logger.LogDebug("Worker notify: channel={Channel}, payload={Payload}",
                e.Channel, e.Payload);
        }
    }

    private async Task HandleCancelNotificationAsync(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || !int.TryParse(payload, out var commandId))
        {
            logger.LogWarning("Cancel notify ignored: reason=invalid_payload");
            return;
        }

        logger.LogInformation("Cancel requested: commandId={CommandId}", commandId);

        // Отменяем per-command CTS, чтобы ExecuteOneAsync не перезаписал статус
        if (_commandCts.TryRemove(commandId, out var cts))
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException) { }
            finally
            {
                cts.Dispose();
            }
        }

        if (_activeProcesses.TryRemove(commandId, out var process))
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    logger.LogInformation("Cancel executed: commandId={CommandId}, processId={ProcessId}",
                        commandId, process.Id);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cancel failed to kill process: commandId={CommandId}", commandId);
            }
            finally
            {
                process.Dispose();
            }
        }
        else
        {
            logger.LogDebug("Cancel skipped: commandId={CommandId}, reason=process_not_found_or_already_completed",
                commandId);
        }
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
        var threshold = Array.Find(_partitionThresholds, t => cmd.Priority >= t);

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

    private async Task ExecuteOneAsync(PendingCommand cmd, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        Process? process = null;

        try
        {
            if (!_workerOptions.Commands.TryGetValue(cmd.CommandText, out var commandCfg))
            {
                logger.LogWarning("Command failed: id={Id}, command={Cmd}, reason=unknown_command", cmd.CommandId, cmd.CommandText);
                await dataService.UpdateCommandStatusAsync(cmd.CommandId, CommandStatuses.Failed,
                    errorMessage: $"Unknown command type: {cmd.CommandText}");
                return;
            }

            if (!ValidateFilePath(cmd, commandCfg))
            {
                logger.LogWarning("Command failed: id={Id}, command={Cmd}, reason=invalid_file", cmd.CommandId, cmd.CommandText);
                await dataService.UpdateCommandStatusAsync(cmd.CommandId, CommandStatuses.Failed,
                    errorMessage: $"File validation failed for path: {cmd.FilePath}");
                return;
            }

            var (resolvedPath, resolutionError) = await ResolveExecutablePathAsync(cmd, commandCfg.ExecutablePath, cmd.CommandText, ct);
            if (resolvedPath == null)
            {
                logger.LogWarning("Command failed: id={Id}, command={Cmd}, reason=executable_not_found, error={Error}",
                    cmd.CommandId, cmd.CommandText, resolutionError);
                await dataService.UpdateCommandStatusAsync(cmd.CommandId, CommandStatuses.Failed,
                    errorMessage: resolutionError);
                await dataService.NotifyCommandCompletedAsync(cmd.UserId, cmd.CommandId, cmd.CommandText, CommandStatuses.Failed, resolutionError);
                return;
            }

            var startInfo = CreateProcessStartInfo(cmd, commandCfg);
            startInfo.FileName = resolvedPath;
            logger.LogInformation("Command start: id={Id}, command={Cmd}, attempt={Attempt}",
                cmd.CommandId, cmd.CommandText, cmd.RetryCount + 1);

            // Создаём linked CTS для возможности отмены команды пользователем
            var cmdCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _commandCts[cmd.CommandId] = cmdCts;
            var cmdCt = cmdCts.Token;

            // Перепроверяем статус из БД — мог быть отменён пользователем после ClaimPendingCommandsAsync
            // Ранний return внутри try — finally сам почистит _commandCts и _activeProcesses
            var currentStatus = await dataService.GetCommandStatusAsync(cmd.CommandId);
            if (currentStatus == CommandStatuses.Cancelled)
            {
                logger.LogInformation(
                    "Command cancelled before start: id={Id}, command={Cmd}",
                    cmd.CommandId, cmd.CommandText);
                return;
            }

            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Start();
            _activeProcesses[cmd.CommandId] = process;
            await dataService.UpdateCommandStatusAsync(cmd.CommandId, CommandStatuses.Processing, process.Id);

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

            // Проверяем, не была ли команда отменена пользователем через NOTIFY command_cancel
            if (cmdCt.IsCancellationRequested)
            {
                logger.LogInformation("Command cancelled by user: id={Id}, command={Cmd}, elapsedMs={ElapsedMs}",
                    cmd.CommandId, cmd.CommandText, sw.ElapsedMilliseconds);
                sw.Stop();
                // Статус уже обновлён на 'Cancelled' сервером, ничего не делаем
                return;
            }

            if (!completed)
            {
                process.Kill(true);
                try
                {
                    using var killTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(killTimeout.Token);
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning(
                        "Kill timeout after process timeout: commandId={Id}, pid={Pid}",
                        cmd.CommandId, process.Id);
                }
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
                await dataService.UpdateCommandStatusAsync(cmd.CommandId, CommandStatuses.Done);
                logger.LogInformation("Command done: id={Id}, command={Cmd}, elapsedMs={ElapsedMs}",
                    cmd.CommandId, cmd.CommandText, sw.ElapsedMilliseconds);
                await dataService.NotifyCommandCompletedAsync(cmd.UserId, cmd.CommandId, cmd.CommandText, CommandStatuses.Done, null);
            }
            else if (errorMessage != null)
            {
                await HandleCommandFailureAsync(cmd, errorMessage, null, sw);
            }
        }
        catch (OperationCanceledException)
        {
            if (process != null && !process.HasExited)
            {
                logger.LogInformation(
                    "Process left running on Worker shutdown: commandId={Id}, cmd={Cmd}, pid={Pid}",
                    cmd.CommandId, cmd.CommandText, process.Id);
            }

            throw;
        }
        catch (Exception ex)
        {
            await HandleCommandFailureAsync(cmd, ex.Message, ex, sw);
        }
        finally
        {
            _=_activeProcesses.TryRemove(cmd.CommandId, out _);
            if (_commandCts.TryRemove(cmd.CommandId, out var cmdCts))
            {
                cmdCts.Dispose();
            }
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
    private async Task<(string? resolvedPath, string? errorMessage)> ResolveExecutablePathAsync(
        PendingCommand cmd, string configuredPath, string commandText, CancellationToken ct)
    {
        if (commandText is "PDF" or "DWG" or "IFC" or "BIMDOC")
        {
            try
            {
                var version = await versionDetector.DetectVersionAsync(cmd.FilePath!, ct);
                if (version?.ExecutablePath != null)
                {
                    logger.LogDebug("Resolved {Cmd} executable via BimLib: {Path} (Revit {Year})",
                        commandText, version.ExecutablePath, version.Year);
                    return (version.ExecutablePath, null);
                }

                // Версия определена, но Revit не установлен — понятная ошибка пользователю
                if (version != null && version.ExecutablePath == null)
                {
                    var msg = $"Revit {version.Year} не установлен на сервере. Пожалуйста, установите Revit {version.Year} или обратитесь к администратору.";
                    logger.LogWarning("Could not resolve {Cmd}: {Msg}", commandText, msg);
                    return (null, msg);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "BimLib version detection failed for {Cmd}, falling back to configured path",
                    commandText);
            }
        }

        if (commandText is "NWC" or "CLASHREP")
        {
            try
            {
                var versions = navisworksPathResolver.GetInstalledVersions();
                if (versions.Count > 0)
                {
                    var nwPath = navisworksPathResolver.ResolveFileConvertPath(versions[0])
                                  ?? navisworksPathResolver.ResolveNavisworksPath(versions[0]);
                    if (nwPath != null)
                    {
                        logger.LogDebug("Resolved {Cmd} executable via BimLib: {Path} (Navisworks {Year})",
                            commandText, nwPath, versions[0]);
                        return (nwPath, null);
                    }
                }

                // Navisworks не установлен — понятная ошибка пользователю
                var msg = "Navisworks не установлен на сервере. Пожалуйста, установите Navisworks или обратитесь к администратору.";
                logger.LogWarning("Could not resolve {Cmd}: {Msg}", commandText, msg);
                return (null, msg);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "BimLib Navisworks resolution failed for {Cmd}, falling back to configured path",
                    commandText);
            }
        }

        return (configuredPath, null);
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
            var newRetryCount = await dataService.ScheduleRetryAsync(
                cmd.CommandId, nextRetryAt, errorMessage);
            logger.LogWarning(ex, "Command {Cmd} ({Id}) failed (attempt {Attempt}/{Max}), retry at {Next}. Error: {Msg}",
                cmd.CommandText, cmd.CommandId, newRetryCount, _workerOptions.MaxRetries,
                nextRetryAt.ToString("O"), errorMessage);
            await dataService.NotifyNewCommandsAsync(cmd.SessionId);
        }
        else
        {
            await dataService.UpdateCommandStatusAsync(cmd.CommandId, CommandStatuses.Failed, errorMessage: errorMessage);
            logger.LogError(ex, "Command {Cmd} ({Id}) failed after {Attempt} attempts. Error: {Msg}",
                cmd.CommandText, cmd.CommandId, cmd.RetryCount + 1, errorMessage);
            await dataService.NotifyCommandCompletedAsync(cmd.UserId, cmd.CommandId, cmd.CommandText, CommandStatuses.Failed, errorMessage);
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
