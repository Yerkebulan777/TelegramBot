using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Data;
using TelegramBot.Worker.BimLib.Services;
using TelegramBot.Worker.Schemas;
namespace TelegramBot.Worker.Services;

/// <summary>
/// Подготавливает команду к выполнению: валидация FilePath, резолвинг BIM-исполняемых файлов,
/// создание ProcessStartInfo.
/// </summary>
public sealed class CommandPreparer(
    IOptions<WorkerOptions> workerOptions,
    IOptions<FileSystemOptions> fileSystemOptions,
    IConfiguration configuration,
    CommandDataService commandDataService,
    RevitVersionDetector versionDetector,
    NavisworksPathResolver navisworksPathResolver,
    ILogger<CommandPreparer> logger)
{
    private readonly WorkerOptions _workerOptions = workerOptions.Value;
    private readonly string _taskDirectory = fileSystemOptions.Value.GetEffectiveTaskDirectory();
    private readonly string? _fileSystemRoot = configuration.GetSection(FileSystemOptions.SectionName)[nameof(FileSystemOptions.RootPath)];
    private static readonly XmlSerializer TaskFileSerializer = new(typeof(TaskFile));
    private static readonly XmlSerializerNamespaces EmptyXmlNamespaces = new([XmlQualifiedName.Empty]);
    private const string RevitRussianLanguageArguments = "/language RUS";

    /// <summary>
    /// Возвращает пути к task-файлу и result-файлу для указанной команды.
    /// Используется как CommandPreparer'ом при записи task-файла и ProcessRunner'ом при чтении result-файла,
    /// чтобы оба компонента использовали одну и ту же директорию (настраиваемую через <c>FileSystem:TaskDirectory</c>).
    /// Схема имени — <c>task_{projectName}_{commandId}.xml</c> — 1:1 с эталоном RevitBIMFusion
    /// (см. BimPluginContract.md §TaskFile Location); AddIn имя файла не парсит, читает путь из environment variable,
    /// так что схема — чисто диагностическая, retry одной команды перезаписывает файл предыдущей попытки.
    /// </summary>
    public (string resultFilePath, string taskFilePath) GetTaskFilePaths(int commandId, string filePath)
    {
        var projectName = GetProjectName(filePath);
        var resultFilePath = Path.Combine(_taskDirectory, $"result_{projectName}_{commandId}.xml");
        var taskFilePath = Path.Combine(_taskDirectory, $"task_{projectName}_{commandId}.xml");
        return (resultFilePath, taskFilePath);
    }

    private static string GetProjectName(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(name))
        {
            return "unknown";
        }

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalidChar, '_');
        }

        return name;
    }

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

        var (resolvedPath, resolutionError) = ResolveExecutablePath(cmd, commandCfg.ExecutablePath, cmd.CommandText, ct);
        if (resolvedPath == null)
        {
            logger.LogWarning("Command failed: id={Id}, correlationId={CorrelationId}, command={Cmd}, reason=executable_not_found, error={Error}",
                cmd.CommandId, cmd.CorrelationId, cmd.CommandText, resolutionError);
            _ = await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Failed,
                errorMessage: resolutionError);
            return null;
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
        return resultCfg;
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

            if (!string.IsNullOrWhiteSpace(_fileSystemRoot) && !IsPathWithinRoot(fullPath, _fileSystemRoot))
            {
                logger.LogWarning("Validation failed: path outside configured root '{File}' ({Id}), root='{Root}'",
                    cmd.FilePath, cmd.CommandId, _fileSystemRoot);
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

        try
        {
            if (File.GetAttributes(cmd.FilePath).HasFlag(FileAttributes.ReparsePoint))
            {
                logger.LogWarning("Validation failed: reparse point file is not allowed '{File}' ({Id})",
                    cmd.FilePath, cmd.CommandId);
                return false;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Validation failed: cannot read file attributes '{File}' ({Id})",
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
            logger.LogWarning("Validation failed: extension '{Ext}' not allowed for {Cmd} ({Id}). Allowed: {Allowed}",
                ext, cmd.CommandText, cmd.CommandId, string.Join(", ", cfg.AllowedExtensions));
            return false;
        }

        return true;
    }

    private static bool IsPathWithinRoot(string fullPath, string rootPath)
    {
        try
        {
            var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException or System.Security.SecurityException)
        {
            // Path.GetFullPath бросает конкретные типы при невалидном пути.
            // Трактовка: путь за пределами root (defensive default для security-check).
            return false;
        }
    }

    /// <summary>Пытается определить версию Revit/Navisworks через BimLib и вернуть полный путь к исполняемому файлу.</summary>
    private (string? resolvedPath, string? errorMessage) ResolveExecutablePath(
        PendingCommand cmd, string configuredPath, string commandText, CancellationToken ct)
    {
        if (IsRevitCommand(commandText))
        {
            return ResolveRevitPath(cmd, commandText, ct) ?? (configuredPath, null);
        }

        if (commandText is CommandCodes.ClashRep)
        {
            return ResolveNavisworksPath(commandText) ?? (configuredPath, null);
        }

        return (configuredPath, null);
    }

    /// <summary>Резолвит путь к Revit.exe через BimLib.</summary>
    private (string? resolvedPath, string? errorMessage)? ResolveRevitPath(
        PendingCommand cmd, string commandText, CancellationToken ct)
    {
        try
        {
            var version = versionDetector.DetectVersion(cmd.FilePath!, ct);
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
            var path = navisworksPathResolver.ResolveLatestExecutable();
            if (path == null)
            {
                var msg = "Navisworks не установлен на сервере. Пожалуйста, установите Navisworks или обратитесь к администратору.";
                logger.LogWarning("Could not resolve {Cmd}: {Msg}", commandText, msg);
                return (null, msg);
            }

            logger.LogDebug("Resolved {Cmd} executable via BimLib: {Path}", commandText, path);
            return (path, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "BimLib Navisworks resolution failed for {Cmd}, falling back to configured path",
                commandText);
        }

        return null;
    }

    /// <summary>Создаёт ProcessStartInfo из конфигурации команды.</summary>
    public ProcessStartInfo CreateProcessStartInfo(PendingCommand cmd, CommandConfig cfg)
    {
        var (resultFilePath, taskFilePath) = GetTaskFilePaths(cmd.CommandId, cmd.FilePath ?? string.Empty);

        var isRevitCommand = IsRevitCommand(cmd.CommandText);
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

    public static bool IsRevitCommand(string commandText)
    {
        return commandText is CommandCodes.Pdf
            or CommandCodes.Dwg
            or CommandCodes.Nwc
            or CommandCodes.Data
            or CommandCodes.Ifc
            or CommandCodes.BimDoc;
    }

    /// <summary>
    /// Создаёт файл задания <c>task_{projectName}_{commandId}.xml</c> для BIM-плагина (контракт
    /// <c>…\RevitBIMFusion\Docs\BimPluginContract.md</c> §TaskFile Location). Плагин читает этот файл,
    /// чтобы получить <c>commandText</c>, <c>filePath</c> и <c>resultFilePath</c>. Revit AddIn получает
    /// путь к TaskFile через process-scoped environment variable, а <c>filePath</c> намеренно НЕ передаётся
    /// в CLI — плагин открывает .rvt сам с <c>Audit=true</c>/<c>DetachAndPreserveWorksets</c>.
    /// Поэтому если запись task-файла провалилась — AddIn не сможет корректно выполнить команду.
    /// </summary>
    /// <returns>
    /// <c>true</c> если task-файл успешно записан, <c>false</c> если запись не удалась (директория
    /// не создана, .tmp не записан, или rename не прошёл). В последнем случае вызывающая сторона
    /// может решить — стартовать процесс всё равно или прервать выполнение.
    /// </returns>
    public bool CreateTaskFile(PendingCommand cmd)
    {
        var (resultFilePath, taskFilePath) = GetTaskFilePaths(cmd.CommandId, cmd.FilePath ?? string.Empty);

        try
        {
            // Гарантируем, что директория существует.
            _=Directory.CreateDirectory(_taskDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            logger.LogError(ex,
                "Failed to create task directory '{Dir}' for command {Id} (correlationId={CorrelationId}, command={Cmd}). " +
                "AddIn will not be able to read the task file. Process start will likely fail or produce wrong results.",
                _taskDirectory, cmd.CommandId, cmd.CorrelationId, cmd.CommandText);
            return false;
        }

        var task = new TaskFile
        {
            CommandId = cmd.CommandId,
            CommandText = cmd.CommandText,
            FilePath = cmd.FilePath ?? string.Empty,
            ResultFilePath = resultFilePath,
        };

        try
        {
            // Atomic write: пишем во временный файл, затем переименовываем
            var tmpPath = taskFilePath + ".tmp";
            var settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                Indent = true,
            };

            using (var writer = XmlWriter.Create(tmpPath, settings))
            {
                TaskFileSerializer.Serialize(writer, task, EmptyXmlNamespaces);
            }

            // Runtime-валидация по XSD перед atomic Move: ловит drift между C#-моделью TaskFile
            // и XML-контрактом (…\RevitBIMFusion\Docs\TaskFile.schema.xsd) до того, как файл
            // попадёт к плагину. При ошибке — abort (команда упадёт на старте, а не в плагине).
            List<string>? validationErrors = null;
            using (var reader = XmlReader.Create(tmpPath))
            {
                validationErrors = TaskFileValidator.Validate(reader);
            }

            if (validationErrors.Count > 0)
            {
                try { File.Delete(tmpPath); }
                catch (Exception delEx) when (delEx is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(delEx, "Failed to delete invalid tmp task file '{TmpPath}'", tmpPath);
                }

                logger.LogError(
                    "Task file XSD validation failed for command {Id} (correlationId={CorrelationId}, command={Cmd}). " +
                    "Drift between C# model and XSD contract. Errors: {Errors}",
                    cmd.CommandId, cmd.CorrelationId, cmd.CommandText, string.Join("; ", validationErrors));
                return false;
            }

            File.Move(tmpPath, taskFilePath, overwrite: true);

            logger.LogDebug(
                "Task file created: commandId={CommandId}, taskFile={TaskFilePath}, resultFile={ResultFilePath}",
                cmd.CommandId, taskFilePath, resultFilePath);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            // Запись task-файла не удалась. AddIn не получит filePath (контракт запрещает
            // передачу .rvt-пути в CLI args), поэтому команда почти наверняка упадёт. Логируем громко
            // с correlationId, чтобы в случае end-to-end проблем можно было быстро найти эту запись.
            logger.LogError(ex,
                "Failed to create task file '{TaskFilePath}' for command {Id} (correlationId={CorrelationId}, command={Cmd}). " +
                "AddIn will not receive filePath; result file will likely not be written. " +
                "Check FileSystem:TaskDirectory permissions, disk space, and antivirus interference.",
                taskFilePath, cmd.CommandId, cmd.CorrelationId, cmd.CommandText);
            return false;
        }
    }

    /// <summary>
    /// Очищает временные файлы task и result для указанной команды.
    /// </summary>
    public void CleanupTempFiles(int commandId, string filePath)
    {
        try
        {
            var (resultFilePath, taskFilePath) = GetTaskFilePaths(commandId, filePath);
            var deletedCount = 0;

            if (File.Exists(taskFilePath))
            {
                File.Delete(taskFilePath);
                deletedCount++;
            }

            if (File.Exists(resultFilePath))
            {
                File.Delete(resultFilePath);
                deletedCount++;
            }

            logger.LogDebug("Temp file cleanup: commandId={CommandId}, deleted={DeletedCount}",
                commandId, deletedCount);
        }
        catch (Exception ex)
        {
            // Best effort — не должны падать из-за ошибки очистки
            logger.LogDebug(ex, "Failed to clean up temp files for command {CommandId}", commandId);
        }
    }
}
