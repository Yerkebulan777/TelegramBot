using System.Collections.Concurrent;
using System.Diagnostics;
using Dapper;
using Npgsql;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Background service: ожидает уведомления через Postgres LISTEN/NOTIFY,
/// при получении сигнала проверяет БД на наличие новых команд и выполняет их.
/// Реализует пул процессов с лимитами для защиты от перегрузки системы.
/// Автоматически переподключается при потере соединения.
/// 
/// TODO (ОБЯЗАТЕЛЬНО): Реализовать концепцию партиций (логических очередей)
/// - Поле Partition уже добавлено в БД
/// - Требуется: per-partition пул процессов, маппинг команд, конфигурация
/// - См. документацию: Docs/command-execution-algorithm.md раздел "Партиции"
/// </summary>
public sealed class CommandExecutionService(
    IDataService dataService,
    IConfiguration configuration,
    ILogger<CommandExecutionService> logger) : BackgroundService
{
    private const int FallbackTimeoutSec = 300; // 5 мин — safety net, если NOTIFY потерян
    private const int DefaultBatchSize = 50;
    private const int LeaseTimeoutMin = 5;
    private const int ReconnectDelayMs = 5_000; // 5 сек между попытками переподключения

    // Настройки пула процессов
    private const int MaxConcurrentProcesses = 5; // Глобальный лимит одновременных процессов
    private const int ProcessTimeoutSec = 3600; // 1 час — максимальное время выполнения команды
    private const int CleanupIntervalSec = 60; // Интервал очистки истёкших lease

    private readonly string _connectionString = configuration.GetConnectionString("Postgres")
        ?? "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres";

    // Пул процессов: семафор для ограничения параллелизма
    private readonly SemaphoreSlim _processPool = new(MaxConcurrentProcesses, MaxConcurrentProcesses);
    
    // Трекинг активных процессов для возможности принудительного завершения
    private readonly ConcurrentDictionary<int, ProcessContext> _activeProcesses = new();
    
    // CancellationTokenSource для graceful shutdown
    private CancellationTokenSource? _shutdownCts;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Worker starting with max {Max} concurrent processes...", MaxConcurrentProcesses);
        
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
                    await dataService.ReleaseTimeoutCommandsAsync(ProcessTimeoutSec);
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
                    logger.LogError(ex, "Connection lost. Reconnecting in {Delay}ms...", ReconnectDelayMs);
                    await Task.Delay(ReconnectDelayMs, stoppingToken);
                }
            }
        }
        finally
        {
            // Graceful shutdown: ждём завершения активных процессов
            logger.LogInformation("Shutting down, waiting for active processes to complete...");
            await WaitForActiveProcessesAsync();
            _shutdownCts?.Dispose();
            _processPool.Dispose();
        }

        logger.LogInformation("Worker stopped");
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
            foreach (var ctx in _activeProcesses.Values)
            {
                try
                {
                    ctx.Process.Kill(true); // Убить дерево процессов
                }
                catch { /* Игнорируем ошибки при завершении */ }
            }
        }
    }

    private async Task RunListenerLoopAsync(CancellationToken stoppingToken)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(stoppingToken);

        await conn.ExecuteAsync("LISTEN new_command;");

        conn.Notification += OnNotificationReceived;

        logger.LogInformation("Connected. Listening for NOTIFY on 'new_command'...");

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
            catch (OperationCanceledException) { throw; }
            catch (TimeoutException)
            {
                // Fallback poll — если NOTIFY был потерян
                logger.LogDebug("Fallback poll: checking for pending commands");
            }
            catch (NpgsqlException ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "PostgreSQL connection error, reconnecting...");
                throw; // Выходим из цикла → outer reconnect
            }

            // Освобождаем истёкшие Lease перед каждым циклом
            await dataService.ReleaseExpiredLeasesAsync();
            await ProcessBatchAsync(stoppingToken);
        }
    }

    private void OnNotificationReceived(object sender, NpgsqlNotificationEventArgs e)
    {
        logger.LogDebug("NOTIFY received: channel='{Channel}', payload='{Payload}'",
            e.Channel, e.Payload);
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        try
        {
            var claimed = await dataService.ClaimPendingCommandsAsync(DefaultBatchSize);

            if (claimed.Count == 0)
                return;

            logger.LogInformation("Claimed {Count} commands for processing", claimed.Count);

            // Запускаем все команды параллельно, но с ограничением пула
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
        // Ждём свободного слота в пуле (блокирующее ожидание)
        await _processPool.WaitAsync(ct);
        
        try
        {
            // Проверяем, не отмена ли это
            if (ct.IsCancellationRequested)
                return;
            
            // Запускаем выполнение в фоне
            _ = ExecuteOneAsync(cmd, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error acquiring process pool slot for command {CommandId}", cmd.CommandId);
            _processPool.Release();
        }
    }

    private async Task ExecuteOneAsync(PendingCommand cmd, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        Process? process = null;

        try
        {
            ProcessStartInfo? startInfo = cmd.CommandText switch
            {
                "PDF" or "DWG" or "IFC" or "BIMDOC" => CreateRevitProcessStartInfo(cmd),
                "NWC" or "CLASHREP" => CreateNavisworksProcessStartInfo(cmd),
                "AUTORES" => CreateAiAgentProcessStartInfo(cmd),
                _ => null
            };

            if (startInfo == null)
            {
                logger.LogWarning("Unknown command '{Cmd}' ({Id})", cmd.CommandText, cmd.CommandId);
                await dataService.UpdateCommandStatusAsync(cmd.CommandId, CommandStatuses.Failed, 
                    errorMessage: $"Unknown command type: {cmd.CommandText}");
                return;
            }

            // Запуск процесса
            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Start();
            
            // Сохраняем контекст для отслеживания
            var context = new ProcessContext(process, cmd.CommandId, sw);
            _activeProcesses[cmd.CommandId] = context;
            
            // Обновляем статус с PID
            await dataService.UpdateCommandStatusAsync(cmd.CommandId, CommandStatuses.Processing, process.Id);
            
            logger.LogInformation("Started process {Pid} for {Cmd} / {File} ({Id})", 
                process.Id, cmd.CommandText, cmd.FilePath, cmd.CommandId);

            // Ожидание с таймаутом
            var timeout = TimeSpan.FromSeconds(ProcessTimeoutSec);
            var completed = await Task.Run(() => process.WaitForExit((int)timeout.TotalMilliseconds), ct);

            if (!completed)
            {
                // Таймаут: убиваем процесс и всё дерево потомков
                logger.LogWarning("Timeout: killing process {Pid} for command {Id}", process.Id, cmd.CommandId);
                process.Kill(true); // true = kill entire process tree
                await process.WaitForExitAsync(ct);
                
                await dataService.UpdateCommandStatusAsync(cmd.CommandId, CommandStatuses.Failed,
                    errorMessage: $"Timeout: process exceeded {ProcessTimeoutSec}s limit");
                return;
            }

            // Проверяем код выхода
            if (process.ExitCode == 0)
            {
                sw.Stop();
                await dataService.UpdateCommandStatusAsync(cmd.CommandId, CommandStatuses.Done);
                logger.LogInformation("Done: {Cmd} / {File} ({Id}) — {Ms}ms, exit code: {ExitCode}",
                    cmd.CommandText, cmd.FilePath, cmd.CommandId, sw.ElapsedMilliseconds, process.ExitCode);
            }
            else
            {
                sw.Stop();
                await dataService.UpdateCommandStatusAsync(cmd.CommandId, CommandStatuses.Failed,
                    errorMessage: $"Process exited with code {process.ExitCode}");
                logger.LogWarning("Failed: {Cmd} / {File} ({Id}) — exit code: {ExitCode}",
                    cmd.CommandText, cmd.FilePath, cmd.CommandId, process.ExitCode);
            }
        }
        catch (OperationCanceledException) 
        {
            // Отмена: убиваем процесс если он ещё активен
            if (process != null && !process.HasExited)
            {
                logger.LogWarning("Cancelled: killing process {Pid} for command {Id}", process.Id, cmd.CommandId);
                process.Kill(true);
            }
            throw; 
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex, "Failed: {Cmd} / {File} ({Id})", cmd.CommandText, cmd.FilePath, cmd.CommandId);
            await dataService.UpdateCommandStatusAsync(cmd.CommandId, CommandStatuses.Failed,
                errorMessage: ex.Message);
        }
        finally
        {
            // Освобождаем слот в пуле
            _processPool.Release();
            
            // Удаляем из трекинга
            _activeProcesses.TryRemove(cmd.CommandId, out _);
        }
    }

    /// <summary>Контекст выполняющегося процесса для трекинга.</summary>
    private sealed class ProcessContext
    {
        public Process Process { get; }
        public int CommandId { get; }
        public Stopwatch Stopwatch { get; }

        public ProcessContext(Process process, int commandId, Stopwatch stopwatch)
        {
            Process = process;
            CommandId = commandId;
            Stopwatch = stopwatch;
        }
    }

    private ProcessStartInfo CreateRevitProcessStartInfo(PendingCommand cmd)
    {
        // TODO: настроить путь к Revit.exe и аргументы
        // Пример: Revit.exe /language eng-USA /al "C:\Path\To\Addin.addin"
        return new ProcessStartInfo
        {
            FileName = "Revit.exe",
            Arguments = $"/command \"{cmd.CommandText}\" \"{cmd.FilePath}\"",
            WorkingDirectory = Path.GetDirectoryName(cmd.FilePath) ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    private ProcessStartInfo CreateNavisworksProcessStartInfo(PendingCommand cmd)
    {
        // TODO: настроить путь к Navisworks FileConvert.exe или COM API
        return new ProcessStartInfo
        {
            FileName = "FileConvert.exe",
            Arguments = $"/command \"{cmd.CommandText}\" \"{cmd.FilePath}\"",
            WorkingDirectory = Path.GetDirectoryName(cmd.FilePath) ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    private ProcessStartInfo CreateAiAgentProcessStartInfo(PendingCommand cmd)
    {
        // TODO: настроить путь к скрипту AI-агента или HTTP-клиент
        return new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"ai_agent.py --command \"{cmd.CommandText}\" --file \"{cmd.FilePath}\"",
            WorkingDirectory = Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }
}

