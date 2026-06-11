using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;
using TelegramBot.Worker.BimLib.Interfaces;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Подготавливает команду к выполнению: валидация FilePath, резолвинг BIM-исполняемых файлов,
/// создание ProcessStartInfo.
/// </summary>
public sealed class CommandPreparer(
    IOptions<WorkerOptions> workerOptions,
    ICommandDataService commandDataService,
    IRevitVersionDetector versionDetector,
    INavisworksPathResolver navisworksPathResolver,
    ILogger<CommandPreparer> logger)
{
    private readonly WorkerOptions _workerOptions = workerOptions.Value;

    /// <summary>
    /// Проверяет команду: тип, файл, BIM-исполняемый файл.
    /// Если подготовка не удалась — записывает Failed в БД и возвращает null.
    /// </summary>
    public async Task<CommandConfig?> PrepareAsync(PendingCommand cmd, CancellationToken ct)
    {
        if (!_workerOptions.Commands.TryGetValue(cmd.CommandText, out var commandCfg))
        {
            logger.LogWarning("Command failed: id={Id}, correlationId={CorrelationId}, command={Cmd}, reason=unknown_command",
                cmd.CommandId, cmd.CorrelationId, cmd.CommandText);
            _ = await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Failed,
                errorMessage: $"Unknown command type: {cmd.CommandText}");
            return null;
        }

        if (!ValidateFilePath(cmd, commandCfg))
        {
            logger.LogWarning("Command failed: id={Id}, correlationId={CorrelationId}, command={Cmd}, reason=invalid_file",
                cmd.CommandId, cmd.CorrelationId, cmd.CommandText);
            _ = await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Failed,
                errorMessage: $"File validation failed for path: {cmd.FilePath}");
            return null;
        }

        var (resolvedPath, resolutionError) = await ResolveExecutablePathAsync(cmd, commandCfg.ExecutablePath, cmd.CommandText, ct);
        if (resolvedPath == null)
        {
            logger.LogWarning("Command failed: id={Id}, correlationId={CorrelationId}, command={Cmd}, reason=executable_not_found, error={Error}",
                cmd.CommandId, cmd.CorrelationId, cmd.CommandText, resolutionError);
            _ = await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Failed,
                errorMessage: resolutionError);
            return null;
        }

        commandCfg.ExecutablePath = resolvedPath;
        return commandCfg;
    }

    /// <summary>Валидация FilePath: существование файла, расширение, path traversal.</summary>
    public bool ValidateFilePath(PendingCommand cmd, CommandConfig cfg)
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

        if (cfg.AllowedExtensions is not { Count: > 0 })
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

    /// <summary>Пытается определить версию Revit/Navisworks через BimLib и вернуть полный путь к исполняемому файлу.</summary>
    private async Task<(string? resolvedPath, string? errorMessage)> ResolveExecutablePathAsync(
        PendingCommand cmd, string configuredPath, string commandText, CancellationToken ct)
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

    /// <summary>Резолвит путь к Revit.exe через BimLib.</summary>
    private async Task<(string? resolvedPath, string? errorMessage)?> ResolveRevitPathAsync(
        PendingCommand cmd, string commandText, CancellationToken ct)
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

    /// <summary>Резолвит путь к Navisworks/FileConvert.exe через BimLib.</summary>
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

    /// <summary>Создаёт ProcessStartInfo из конфигурации команды.</summary>
    public static ProcessStartInfo CreateProcessStartInfo(PendingCommand cmd, CommandConfig cfg)
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
