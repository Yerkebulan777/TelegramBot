using System.ComponentModel;
using System.Diagnostics;
using TelegramBot.Core.Config;
using TelegramBot.Core.Models;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Запускает внешний процесс по конфигурации команды.
/// Отвечает только за Process.Start() и регистрацию в _activeProcesses.
/// </summary>
public sealed class ProcessStarter(
    CommandPreparer commandPreparer,
    ILogger<ProcessStarter> logger)
{
    private readonly SemaphoreSlim _launchGate = new(1, 1);

    /// <summary>
    /// Запускает процесс и возвращает его экземпляр.
    /// </summary>
    public async Task<Process> StartAsync(PendingCommand cmd, CommandConfig commandCfg, CancellationToken ct)
    {
        var (_, taskFilePath) = commandPreparer.GetTaskFilePaths(cmd.CommandId, cmd.FilePath ?? string.Empty);
        if (!commandPreparer.CreateTaskFile(cmd))
        {
            throw new IOException(
                $"Failed to write task file in TaskDirectory '{taskFilePath}'. " +
                $"AddIn cannot proceed without the task file. Check FileSystem:TaskDirectory permissions, disk space, and antivirus.");
        }

        var startInfo = commandPreparer.CreateProcessStartInfo(cmd, commandCfg);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        await _launchGate.WaitAsync(ct);
        try
        {
            _ = process.Start();

            logger.LogInformation(
                "Process started: cmd={Cmd}, id={Id}, corr={CorrelationId}, pid={Pid}, attempt={Attempt}",
                cmd.CommandText, cmd.CommandId, cmd.CorrelationId, process.Id, cmd.RetryCount + 1);

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
}
