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
    CommandTaskFileStore taskFileStore,
    ProcessLaunchGate launchGate,
    ILogger<ProcessStarter> logger)
{
    /// <summary>
    /// Запускает процесс и возвращает его экземпляр.
    /// </summary>
    public async Task<Process> StartAsync(PendingCommand cmd, CommandConfig commandCfg, CancellationToken ct)
    {
        var (resultFilePath, taskFilePath) = taskFileStore.GetPaths(cmd.CommandId, cmd.FilePath ?? string.Empty);
        if (File.Exists(resultFilePath))
        {
            // Retain previous evidence, but never consume it as the result of this attempt.
            File.Move(resultFilePath, resultFilePath + ".previous", overwrite: true);
            logger.LogWarning("Previous result retained before new attempt: id={CommandId}", cmd.CommandId);
        }
        if (!taskFileStore.Create(cmd))
        {
            throw new IOException(
                $"Failed to write task file in TaskDirectory '{taskFilePath}'. " +
                $"AddIn cannot proceed without the task file. Check FileSystem:TaskDirectory permissions, disk space, and antivirus.");
        }

        var startInfo = commandPreparer.CreateProcessStartInfo(cmd, commandCfg);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            var gate = CommandTraits.GetLaunchGate(cmd.CommandText);
            if (gate == ProcessLaunchGateKind.None)
            {
                _ = process.Start();
            }
            else
            {
                await launchGate.StartAsync(process, gate, cmd.CommandId, ct);
            }

            logger.LogInformation(
                "Process started: cmd={Cmd}, id={Id}, corr={CorrelationId}, pid={Pid}, attempt={Attempt}",
                cmd.CommandText, cmd.CommandId, cmd.CorrelationId, process.Id, cmd.RetryCount + 1);

            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }
}
