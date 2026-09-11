using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.BimLib.Services;
namespace TelegramBot.Worker.Services;

/// <summary>
/// Подготавливает команду к выполнению: валидация FilePath, резолвинг BIM-исполняемых файлов,
/// создание ProcessStartInfo.
/// </summary>
public sealed class CommandPreparer(
    IOptions<WorkerOptions> workerOptions,
    IOptions<FileSystemOptions> fileSystemOptions,
    CommandTaskFileStore taskFileStore,
    RevitVersionDetector versionDetector,
    NavisworksPathResolver navisworksPathResolver,
    ILogger<CommandPreparer> logger)
{
    private readonly WorkerOptions _workerOptions = workerOptions.Value;
    private readonly FileSystemOptions _fileSystemOptions = fileSystemOptions.Value;
    private const string RevitRussianLanguageArguments = "/language RUS";
    private const string RevitVersionDetectionError =
        "Не удалось определить версию Revit для файла. Проверьте целостность файла или обратитесь к администратору.";

    /// <summary>
    /// Проверяет команду: тип, файл, BIM-исполняемый файл.
    /// Если подготовка не удалась — возвращает причину. Запись терминального статуса остаётся
    /// обязанностью ProcessRunner, чтобы она была атомарна с session-completed уведомлением.
    /// </summary>
    public Task<CommandPreparationResult> PrepareAsync(PendingCommand cmd, CancellationToken ct)
    {
        if (!_workerOptions.Commands.TryGetValue(cmd.CommandText, out var commandCfg))
        {
            logger.LogWarning("Cmd fail: id={Id}, corr={CorrelationId}, cmd={Cmd}, reason=unknown",
                cmd.CommandId, cmd.CorrelationId, cmd.CommandText);
            return Task.FromResult(CommandPreparationResult.Failed($"Unknown command type: {cmd.CommandText}"));
        }

        if (!ValidateFilePath(cmd, commandCfg))
        {
            logger.LogWarning("Cmd fail: id={Id}, corr={CorrelationId}, cmd={Cmd}, reason=invalid_file",
                cmd.CommandId, cmd.CorrelationId, cmd.CommandText);
            return Task.FromResult(CommandPreparationResult.Failed($"File validation failed for path: {cmd.FilePath}"));
        }

        var (resolvedPath, resolutionError) = ResolveExecutablePath(cmd, commandCfg.ExecutablePath, cmd.CommandText, ct);
        if (resolvedPath == null)
        {
            logger.LogWarning("Cmd fail: id={Id}, corr={CorrelationId}, cmd={Cmd}, reason=exe_not_found, err={Error}",
                cmd.CommandId, cmd.CorrelationId, cmd.CommandText, resolutionError);
            return Task.FromResult(CommandPreparationResult.Failed(
                resolutionError ?? "Executable path could not be resolved."));
        }

        // Создаём копию CommandConfig — НЕ мутируем shared-объект из IOptions!
        // Иначе параллельные команды «загрязняют» ExecutablePath друг друга.
        // https://learn.microsoft.com/en-us/dotnet/core/extensions/options#ios-postconfigure-options
        var resultCfg = new CommandConfig
        {
            ExecutablePath = resolvedPath,
            ArgumentsTemplate = commandCfg.ArgumentsTemplate,
            AllowedExtensions = commandCfg.AllowedExtensions != null
                ? [.. commandCfg.AllowedExtensions]
                : null,
            WorkingDirectory = commandCfg.WorkingDirectory,
        };
        return Task.FromResult(CommandPreparationResult.Ready(resultCfg));
    }

    /// <summary>Валидация FilePath: существование файла, расширение, path traversal.</summary>
    public bool ValidateFilePath(PendingCommand cmd, CommandConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cmd.FilePath))
        {
            logger.LogWarning("Validation: empty path for cmd {Cmd} ({Id})",
                cmd.CommandText, cmd.CommandId);
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(cmd.FilePath);
            if (fullPath != cmd.FilePath && !fullPath.Equals(cmd.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Validation: path traversal '{File}' ({Id})",
                    cmd.FilePath, cmd.CommandId);
                return false;
            }

            var rootPath = string.IsNullOrWhiteSpace(cmd.RootPath)
                ? _fileSystemOptions.RootPath
                : cmd.RootPath;
            if (!string.IsNullOrWhiteSpace(rootPath) && !FileSystemOptions.IsPathWithinRoot(rootPath, fullPath))
            {
                logger.LogWarning("Validation: path outside root '{File}' ({Id}), root='{Root}'",
                    cmd.FilePath, cmd.CommandId, rootPath);
                return false;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Validation: invalid path '{File}' ({Id})",
                cmd.FilePath, cmd.CommandId);
            return false;
        }

        if (!File.Exists(cmd.FilePath))
        {
            logger.LogWarning("Validation: file not found '{File}' ({Cmd}, {Id})",
                cmd.FilePath, cmd.CommandText, cmd.CommandId);
            return false;
        }

        try
        {
            if (File.GetAttributes(cmd.FilePath).HasFlag(FileAttributes.ReparsePoint))
            {
                logger.LogWarning("Validation: reparse point '{File}' ({Id})",
                    cmd.FilePath, cmd.CommandId);
                return false;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Validation: cant read attributes '{File}' ({Id})",
                cmd.FilePath, cmd.CommandId);
            return false;
        }

        if (cfg.AllowedExtensions is not { Count: > 0 })
        {
            return true;
        }

        var ext = Path.GetExtension(cmd.FilePath)?.ToLowerInvariant();
        if (!cfg.AllowedExtensions.Contains(ext ?? ""))
        {
            logger.LogWarning("Validation: ext '{Ext}' not allowed ({Cmd}, {Id}). Allowed: {Allowed}",
                ext, cmd.CommandText, cmd.CommandId, string.Join(", ", cfg.AllowedExtensions));
            return false;
        }

        return true;
    }

    /// <summary>Пытается определить версию Revit/Navisworks через BimLib и вернуть полный путь к исполняемому файлу.</summary>
    private (string? resolvedPath, string? errorMessage) ResolveExecutablePath(
        PendingCommand cmd, string configuredPath, string commandText, CancellationToken ct)
    {
        if (CommandTraits.RequiresRevit(commandText))
        {
            return ResolveRevitPath(cmd, commandText, ct);
        }

        if (commandText is CommandCodes.ClashRep)
        {
            return ResolveNavisworksPath(commandText) ?? (configuredPath, null);
        }

        return (configuredPath, null);
    }

    /// <summary>Резолвит путь к Revit.exe через BimLib.</summary>
    private (string? resolvedPath, string? errorMessage) ResolveRevitPath(
        PendingCommand cmd, string commandText, CancellationToken ct)
    {
        try
        {
            var version = versionDetector.DetectVersion(cmd.FilePath!, ct);
            if (version?.ExecutablePath != null)
            {
                logger.LogDebug("{Cmd} via BimLib: {Path} (Revit {Year})",
                    commandText, version.ExecutablePath, version.Year);
                return (version.ExecutablePath, null);
            }

            if (version != null)
            {
                var msg = $"Revit {version.Year} не установлен на сервере. Пожалуйста, установите Revit {version.Year} или обратитесь к администратору.";
                logger.LogWarning("Resolve fail: {Cmd}: {Msg}", commandText, msg);
                return (null, msg);
            }

            logger.LogWarning("Resolve fail: {Cmd}: {Msg}", commandText, RevitVersionDetectionError);
            return (null, RevitVersionDetectionError);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "BimLib detect fail for {Cmd}", commandText);
            return (null, RevitVersionDetectionError);
        }
    }

    /// <summary>Резолвит путь к Navisworks/FileConvert.exe через BimLib.</summary>
    private (string? resolvedPath, string? errorMessage)? ResolveNavisworksPath(string commandText)
    {
        try
        {
            var path = navisworksPathResolver.ResolveLatestExecutable();
            if (path == null)
            {
                var msg = "Navisworks не установлен на сервере. Пожалуйста, установите Navisworks или обратитесь к администратору.";
                logger.LogWarning("Resolve fail: {Cmd}: {Msg}", commandText, msg);
                return (null, msg);
            }

            logger.LogDebug("{Cmd} via BimLib: {Path}", commandText, path);
            return (path, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Navisworks resolve fail for {Cmd}, fallback", commandText);
        }

        return null;
    }

    /// <summary>Создаёт ProcessStartInfo из конфигурации команды.</summary>
    public ProcessStartInfo CreateProcessStartInfo(PendingCommand cmd, CommandConfig cfg)
    {
        var (resultFilePath, taskFilePath) = taskFileStore.GetPaths(cmd.CommandId, cmd.FilePath ?? string.Empty);

        var isRevitCommand = CommandTraits.RequiresRevit(cmd.CommandText);
        var args = isRevitCommand
            ? RevitRussianLanguageArguments
            : cfg.ArgumentsTemplate
                .Replace("{CommandText}", cmd.CommandText)
                .Replace("{FilePath}", cmd.FilePath)
                .Replace("{CommandId}", cmd.CommandId.ToString())
                .Replace("{TaskFilePath}", taskFilePath)
                .Replace("{ResultFilePath}", resultFilePath);

        var workingDir = cfg.WorkingDirectory switch
        {
            null or "" => Path.GetDirectoryName(cmd.FilePath),
            "." => Environment.CurrentDirectory,
            var dir => dir
        } ?? Environment.CurrentDirectory;

        var startInfo = new ProcessStartInfo
        {
            FileName = cfg.ExecutablePath,
            Arguments = args,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // CP1251, не UTF-8: системные ошибки Win32/Chromium (Revit) на ru-RU Windows
            // пишутся в ANSI-кодировке локали, UTF-8-декодер превращал их в "????" в логах.
            StandardOutputEncoding = Encoding.GetEncoding(1251),
            StandardErrorEncoding = Encoding.GetEncoding(1251)
        };

        if (isRevitCommand)
        {
            startInfo.Environment[WorkerOptions.RevitTaskFileEnvironmentVariable] = Path.GetFullPath(taskFilePath);
        }

        return startInfo;
    }

}
