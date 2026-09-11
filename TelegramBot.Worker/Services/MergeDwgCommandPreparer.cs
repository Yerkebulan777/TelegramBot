using TelegramBot.BimLib.Services;
using TelegramBot.Core.Config;
using TelegramBot.Core.Helpers;
using TelegramBot.Core.Models;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Готовит запуск MERGEDWG: папка экспорта DWG по выбранному RVT, установленный AutoCAD
/// с AutoBIMFusion и script пакетной команды.
/// </summary>
public sealed class MergeDwgCommandPreparer(
    AutoCadPathResolver autoCadPathResolver,
    CommandTaskFileStore taskFileStore,
    ILogger<MergeDwgCommandPreparer> logger)
{
    private const string AutoCadNotFoundError =
        "AutoCAD 2019–2027 с установленным AutoBIMFusion не найден на сервере. Установите AutoCAD и плагин или обратитесь к администратору.";

    public CommandPreparationResult Prepare(PendingCommand cmd, CommandConfig commandCfg)
    {
        var revitFilePath = cmd.FilePath!;

        string exportDirectory;
        try
        {
            exportDirectory = DwgExportPathResolver.ResolveExportDirectory(revitFilePath);

            if (!Directory.Exists(exportDirectory))
            {
                return Failed(cmd, $"Папка экспорта DWG не найдена: {exportDirectory}. Сначала выполните экспорт DWG.");
            }

            if (!DwgExportPathResolver.ContainsDwgFiles(exportDirectory))
            {
                return Failed(cmd, $"В папке экспорта DWG нет файлов .dwg: {exportDirectory}. Сначала выполните экспорт DWG.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Failed(cmd, $"Не удалось прочитать папку экспорта DWG: {ex.Message}");
        }

        var host = autoCadPathResolver.ResolveBatchHost();
        if (host is null)
        {
            return Failed(cmd, AutoCadNotFoundError);
        }

        var (scriptPath, statusPath) = taskFileStore.GetMergeDwgPaths(cmd.CommandId, revitFilePath);
        MergeDwgBatchProtocol.WriteRequest(scriptPath, statusPath, host.PluginPath, exportDirectory);

        logger.LogInformation(
            "MERGEDWG prepared: id={Id}, corr={CorrelationId}, acad={Year}, folder={Folder}, script={Script}",
            cmd.CommandId, cmd.CorrelationId, host.Year, exportDirectory, scriptPath);

        // Аргументы уже резолвнуты: плейсхолдеров шаблона в них нет, подстановка в
        // CreateProcessStartInfo оставляет строку без изменений.
        return CommandPreparationResult.Ready(
            commandCfg.WithResolved(host.ExecutablePath, MergeDwgBatchProtocol.BuildArguments(scriptPath)));
    }

    private CommandPreparationResult Failed(PendingCommand cmd, string errorMessage)
    {
        logger.LogWarning("Cmd fail: id={Id}, corr={CorrelationId}, cmd={Cmd}, err={Error}",
            cmd.CommandId, cmd.CorrelationId, cmd.CommandText, errorMessage);
        return CommandPreparationResult.Failed(errorMessage);
    }
}
