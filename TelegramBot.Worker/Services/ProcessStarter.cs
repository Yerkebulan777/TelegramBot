using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using TelegramBot.Core.Config;
using TelegramBot.Core.Models;
using TelegramBot.Data;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Запускает внешний процесс по конфигурации команды.
/// Отвечает только за Process.Start() и регистрацию в _activeProcesses.
/// </summary>
public sealed class ProcessStarter(
    CommandPreparer commandPreparer,
    CommandDataService commandDataService,
    IOptions<WorkerOptions> workerOptions,
    ILogger<ProcessStarter> logger)
{
    private readonly WorkerOptions _workerOptions = workerOptions.Value;
    private readonly SemaphoreSlim _launchGate = new(1, 1);

    /// <summary>
    /// Запускает процесс и возвращает его экземпляр.
    /// </summary>
    public async Task<Process> StartAsync(PendingCommand cmd, CommandConfig commandCfg, CancellationToken ct)
    {
        var (resultFilePath, taskFilePath) = commandPreparer.GetTaskFilePaths(cmd.CommandId, cmd.FilePath ?? string.Empty);
        if (!commandPreparer.CreateTaskFile(cmd))
        {
            throw new IOException(
                $"Failed to write task file in TaskDirectory '{taskFilePath}'. " +
                $"AddIn cannot proceed without the task file. Check FileSystem:TaskDirectory permissions, disk space, and antivirus.");
        }

        var startInfo = commandPreparer.CreateProcessStartInfo(cmd, commandCfg);

        logger.LogInformation("Command start: id={Id}, correlationId={CorrelationId}, command={Cmd}, attempt={Attempt}",
            cmd.CommandId, cmd.CorrelationId, cmd.CommandText, cmd.RetryCount + 1);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        await _launchGate.WaitAsync(ct);
        try
        {
            _ = process.Start();

            logger.LogInformation(
                "External process start: id={Id}, correlationId={CorrelationId}, exe={ExecutablePath}, args={Arguments}, workingDirectory={WorkingDirectory}, taskFile={TaskFilePath}, expectedResultFile={ResultFilePath}",
                cmd.CommandId, cmd.CorrelationId, startInfo.FileName, startInfo.Arguments, startInfo.WorkingDirectory, taskFilePath, resultFilePath);

            return process;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw;
        }
        finally
        {
            _ = _launchGate.Release();
        }
    }

    /// <summary>
    /// Регистрирует процесс в activeProcesses и настраивает stagger-задержку.
    /// </summary>
    public void RegisterProcess(Process process, int commandId, ConcurrentDictionary<int, Process> activeProcesses)
    {
        activeProcesses[commandId] = process;

        if (_workerOptions.LaunchStaggerSeconds > 0)
        {
            // Держим gate, пока CEF в новом Revit успеет забиндить devtools-порт.
            _ = Task.Delay(TimeSpan.FromSeconds(_workerOptions.LaunchStaggerSeconds), CancellationToken.None);
        }
    }

    /// <summary>
    /// Обновляет статус процесса в БД.
    /// </summary>
    public async Task UpdateProcessStatusAsync(int commandId, int sessionId, string correlationId, long userId, int processId)
    {
        _ = await commandDataService.MarkProcessStartedAndNotifyOnceAsync(
            commandId, processId, sessionId, correlationId, userId);
    }
}
