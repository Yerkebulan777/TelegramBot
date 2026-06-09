using Microsoft.Extensions.Options;
using System.Text;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;
using TelegramBot.Server.Helpers;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Application;

public sealed class JobSubmissionService(
    IDataService dataService,
    ITelegramOutputService outputService,
    IOptions<FileSystemOptions> fileSystemOptions,
    IOptions<RateLimitOptions> rateLimitOptions,
    ILogger<JobSubmissionService> logger) : IJobSubmissionService
{
    private static readonly Dictionary<string, int> _commandPriorityMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [CommandCodes.Pdf] = CommandPriorities.Critical,
        [CommandCodes.Dwg] = CommandPriorities.High,
        [CommandCodes.Nwc] = CommandPriorities.Medium,
        [CommandCodes.Ifc] = CommandPriorities.Medium,
        [CommandCodes.BimDoc] = CommandPriorities.Medium,
        [CommandCodes.ClashRep] = CommandPriorities.Medium,
        [CommandCodes.AutoRes] = CommandPriorities.Low,
    };

    private readonly FileSystemOptions _options = fileSystemOptions.Value;
    private readonly RateLimitOptions _rateLimitOptions = rateLimitOptions.Value;

    public async Task<bool> SubmitJobAsync(long userId, string username, UserSession session, CancellationToken cancellationToken)
    {
        var selectedSections = session.GetSelectedFiles();
        if (selectedSections.Count == 0)
        {
            logger.LogDebug("Job submit blocked: user={UserId}, reason=no_sections_selected", userId);
            await SendWarningAsync(userId, session, "⚠️ Сначала выберите хотя бы один раздел.");
            return false;
        }

        logger.LogDebug(
            "Job submit: user={UserId}, commands={CommandCount}, sections={SectionCount}",
            userId, session.PendingCommand.Count, selectedSections.Count);

        var commandNames = session.PendingCommandName;
        var projectName = GetCurrentProjectName(session);
        var sectionNames = selectedSections
            .Select(PathHelper.GetSafePathName)
            .ToArray();

        var filesToProcess = CollectRvtFiles(selectedSections, cancellationToken);
        if (filesToProcess.Count == 0)
        {
            logger.LogWarning("Job submit blocked: user={UserId}, reason=no_files_found", userId);
            await SendWarningAsync(userId, session, "⚠️ В выбранных разделах не найдены файлы для обработки.");
            return false;
        }

        if (!await CheckDailyFileLimitAsync(userId, session, filesToProcess.Count))
        {
            return false;
        }

        // Проверяем, нет ли уже таких же (команда + файл) в очереди
        if (await dataService.HasDuplicateCommandsAsync(session.PendingCommand, filesToProcess))
        {
            logger.LogWarning("Job blocked: user={UserId}, reason=duplicate_commands_in_queue", userId);
            await SendWarningAsync(userId, session, "⚠️ Эти файлы уже в очереди выполнения.");
            return false;
        }

        var queuedMessage = BuildJobQueuedMessage(commandNames, projectName, sectionNames, filesToProcess.Count);

        var priorities = session.PendingCommand
            .Select(c => _commandPriorityMap.TryGetValue(c, out var p) ? p : CommandPriorities.Default);

        var sessionId = await dataService.CreateSessionWithCommandsAsync(
            session.PendingCommand, filesToProcess, userId, username, filesToProcess.Count, projectName, priorities);

        logger.LogInformation(
            "Job queued: session={SessionId}, user={UserId}, commands={CommandCount}, files={FileCount}",
            sessionId, userId, session.PendingCommand.Count, filesToProcess.Count);

        session.SessionId = checked((int)sessionId);
        await outputService.ClearChatHistoryAsync(userId, session);

        session.ResetNavigation(_options.RootPath);
        session.ClearPendingCommands();
        session.IsFileSelectionActive = false;

        _=await TrackMessageAsync(outputService.RemoveReplyKeyboardAsync(userId, queuedMessage), session);
        return true;
    }

    private async Task<bool> CheckDailyFileLimitAsync(long userId, UserSession session, int newFileCount)
    {
        if (_rateLimitOptions.MaxFilesPerUserPerDay <= 0)
        {
            return true;
        }

        var sinceUtc = DateTime.UtcNow.AddDays(-1);
        var queuedToday = await dataService.CountQueuedFilesByUserSinceAsync(userId, sinceUtc);
        var remaining = _rateLimitOptions.MaxFilesPerUserPerDay - queuedToday;

        if (newFileCount <= remaining)
        {
            return true;
        }

        logger.LogWarning(
            "Job submit blocked: user={UserId}, reason=daily_file_limit, queued={Queued}, requested={Requested}, limit={Limit}",
            userId, queuedToday, newFileCount, _rateLimitOptions.MaxFilesPerUserPerDay);

        var message = remaining > 0
            ? $"⚠️ Дневной лимит файлов: {_rateLimitOptions.MaxFilesPerUserPerDay}. Уже в очереди за 24 часа: {queuedToday}. Можно добавить ещё {remaining}."
            : $"⚠️ Дневной лимит файлов: {_rateLimitOptions.MaxFilesPerUserPerDay}. За последние 24 часа лимит уже исчерпан.";

        await SendWarningAsync(userId, session, message);
        return false;
    }

    private List<string> CollectRvtFiles(IReadOnlySet<string> sectionPaths, CancellationToken cancellationToken)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sectionPath in sectionPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rvtDir = _options.GetRvtPath(sectionPath);
            if (!Directory.Exists(rvtDir))
            {
                logger.LogWarning("RVT directory not found: {RvtDir}", rvtDir);
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(rvtDir))
            {
                if (_options.IsRevitFile(file))
                {
                    _=files.Add(file);
                }
            }
        }

        return [.. files];
    }

    private async Task SendWarningAsync(long userId, UserSession session, string message)
    {
        // This is a simplified version. The UI cleanup logic might still stay in SlashCommandService or move here.
        // For now, let's just send the message.
        _=await TrackMessageAsync(outputService.SendMessageAsync(userId, message), session);
    }

    private async Task<Telegram.Bot.Types.Message?> TrackMessageAsync(Task<Telegram.Bot.Types.Message?> task, UserSession session)
    {
        var msg = await task;
        if (msg != null)
        {
            var sessionId = session.SessionId > 0 ? session.SessionId : (int?)null;
            await dataService.TrackMessageAsync(msg.Chat.Id, msg.MessageId, sessionId);
        }
        return msg;
    }

    private static string BuildJobQueuedMessage(
        IReadOnlyList<string> commandNames,
        string projectName,
        IEnumerable<string> sectionNames,
        int fileCount)
    {
        var builder = new StringBuilder()
            .AppendLine("✅ *Задание успешно добавлено в очередь*")
            .AppendLine()
            .AppendLine("🧰 *Команды*");

        foreach (var commandName in commandNames)
        {
            _=builder.AppendLine($"• {MarkdownHelper.EscapeMarkdown(commandName)}");
        }

        _=builder
            .AppendLine()
            .AppendLine("📌 *Проект*")
            .AppendLine($"`{MarkdownHelper.EscapeMarkdown(projectName)}`")
            .AppendLine()
            .AppendLine("📂 *Разделы*");

        foreach (var sectionName in sectionNames)
        {
            _=builder.AppendLine($"• {MarkdownHelper.EscapeMarkdown(sectionName)}");
        }

        _=builder
            .AppendLine()
            .AppendLine($"📄 *Количество файлов:* `{fileCount}`");

        return builder.ToString();
    }

    private static string GetCurrentProjectName(UserSession session)
    {
        var projectDirectory = Directory.GetParent(session.CurrentPath);
        return projectDirectory == null
            ? PathHelper.GetSafePathName(session.CurrentPath)
            : PathHelper.GetSafePathName(projectDirectory.FullName);
    }
}
