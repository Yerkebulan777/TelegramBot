using Microsoft.Extensions.Options;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using TelegramBot.Core.Config;
using TelegramBot.Core.Models;
using TelegramBot.Worker.Schemas;

namespace TelegramBot.Worker.Services;

/// <summary>Owns the TaskFile and ResultFile lifecycle defined by the BIM plugin contract.</summary>
public sealed class CommandTaskFileStore(
    IOptions<FileSystemOptions> fileSystemOptions,
    ILogger<CommandTaskFileStore> logger)
{
    private readonly string _taskDirectory = fileSystemOptions.Value.GetEffectiveTaskDirectory();
    private static readonly XmlSerializer TaskFileSerializer = new(typeof(TaskFile));
    private static readonly XmlSerializerNamespaces EmptyXmlNamespaces = new([XmlQualifiedName.Empty]);

    public (string ResultFilePath, string TaskFilePath) GetPaths(int commandId, string filePath)
    {
        var projectName = GetProjectName(filePath);
        return (
            Path.Combine(_taskDirectory, $"result_{projectName}_{commandId}.xml"),
            Path.Combine(_taskDirectory, $"task_{projectName}_{commandId}.xml"));
    }

    public bool Create(PendingCommand command)
    {
        var (resultFilePath, taskFilePath) = GetPaths(command.CommandId, command.FilePath ?? string.Empty);

        try
        {
            _ = Directory.CreateDirectory(_taskDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            logger.LogError(ex,
                "Create task dir fail: dir='{Dir}', id={Id}, corr={CorrelationId}, cmd={Cmd}",
                _taskDirectory, command.CommandId, command.CorrelationId, command.CommandText);
            return false;
        }

        var task = new TaskFile
        {
            CommandId = command.CommandId,
            CommandText = command.CommandText,
            FilePath = string.IsNullOrWhiteSpace(command.FilePath) ? string.Empty : Path.GetFullPath(command.FilePath),
            ResultFilePath = Path.GetFullPath(resultFilePath),
            Options = new TaskFileOptions(),
        };

        try
        {
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

            List<string> validationErrors;
            using (var reader = XmlReader.Create(tmpPath))
            {
                validationErrors = TaskFileValidator.Validate(reader);
            }

            if (validationErrors.Count > 0)
            {
                TryDeleteInvalidTemporaryFile(tmpPath);
                logger.LogError(
                    "Task XSD validation fail: id={Id}, corr={CorrelationId}, cmd={Cmd}, err={Errors}",
                    command.CommandId, command.CorrelationId, command.CommandText, string.Join("; ", validationErrors));
                return false;
            }

            File.Move(tmpPath, taskFilePath, overwrite: true);
            logger.LogDebug(
                "Task file: cmdId={CommandId}, task={TaskFilePath}, result={ResultFilePath}",
                command.CommandId, taskFilePath, resultFilePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            logger.LogError(ex,
                "Create task file fail: '{TaskFilePath}', id={Id}, corr={CorrelationId}, cmd={Cmd}",
                taskFilePath, command.CommandId, command.CorrelationId, command.CommandText);
            return false;
        }
    }

    public void Cleanup(int commandId, string filePath)
    {
        try
        {
            var (resultFilePath, taskFilePath) = GetPaths(commandId, filePath);
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

            logger.LogDebug("Temp cleanup: cmdId={CommandId}, deleted={DeletedCount}", commandId, deletedCount);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Temp cleanup fail: cmdId={CommandId}", commandId);
        }
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

    private void TryDeleteInvalidTemporaryFile(string tmpPath)
    {
        try
        {
            File.Delete(tmpPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Delete invalid tmp fail: '{TmpPath}'", tmpPath);
        }
    }
}
