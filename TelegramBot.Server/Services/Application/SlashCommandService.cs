
using Microsoft.Extensions.Options;
using System.Text;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.DTOs;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;
using TelegramBot.Server.Helpers;
using TelegramBot.Server.Interfaces;
using TelegramBot.Server.Models;
using TelegramBot.Server.Services.Application.Handlers;
using Message = Telegram.Bot.Types.Message;

namespace TelegramBot.Server.Services.Application;

public sealed class SlashCommandService(
    IDataService dataService,
    ITelegramOutputService outputService,
    IKeyboardBuilder keyboardBuilder,
    IOptions<FileSystemOptions> fileSystemOptions,
    ILogger<SlashCommandService> logger) : ISlashCommandService
{
    private readonly FileSystemOptions _options = fileSystemOptions.Value;

    public async Task HandleUserCommandAsync(MessageDto message, UserSession session, CancellationToken cancellationToken = default)
    {
        var userId = message.UserId;
        var rawText = message.Text!;
        var username = message.Username!;

        var text = NormalizeCommandText(rawText);

        ArgumentNullException.ThrowIfNullOrWhiteSpace(username);

        logger.LogDebug("Command received: command={Command}, user={UserId}", text, userId);

        if (text.StartsWith('/'))
        {
            await outputService.ClearChatHistoryAsync(userId, session);
        }

        if (text == "/start")
        {
            session.Reset(_options.RootPath);
            var user = await dataService.GetUserAsync(userId);

            if (user?.Status != UserAccessStatus.Approved)
            {
                var adminUser = await dataService.GetUserAsync(userId);
                if (adminUser?.Role == UserRole.Admin && adminUser.Status == UserAccessStatus.Approved)
                {
                    var now = DateTime.UtcNow;
                    await dataService.UpsertUserAsync(new BotUser
                    {
                        UserId = userId,
                        Username = username,
                        Role = UserRole.Admin,
                        Status = UserAccessStatus.Approved,
                        CreatedAt = user?.CreatedAt ?? now,
                        UpdatedAt = now
                    });
                    user = await dataService.GetUserAsync(userId);
                }
            }

            if (user?.Status == UserAccessStatus.Approved)
            {
                await SendHelpMessageAsync(userId, session);
            }
            else
            {
                await SendRegistrationMessageAsync(userId, session);
            }

            return;
        }

        var userRecord = await dataService.GetUserAsync(userId);
        if (userRecord?.Status != UserAccessStatus.Approved)
        {
            logger.LogWarning("Command rejected: command={Command}, user={UserId}, reason=access_denied", text, userId);
            await TrackMessageAsync(outputService.SendMessageAsync(userId, "У вас нет доступа. Введите /start для запроса доступа."), session);
            return;
        }

        if (await HandleCommandSelectionActionsAsync(userId, username, rawText, session, cancellationToken))
        {
            return;
        }

        await HandleSlashCommandAsync(text, message, session, username);
    }

    public async Task<bool> CheckAndNotifyAccessAsync(long userId, UserSession session)
    {
        var userRecord = await dataService.GetUserAsync(userId);

        if (userRecord?.Status == UserAccessStatus.Approved)
        {
            return true;
        }

        await TrackMessageAsync(outputService.SendMessageAsync(userId, "У вас нет доступа. Введите /start для запроса доступа."), session);
        return false;
    }

    private async Task HandleSlashCommandAsync(string command, MessageDto message, UserSession session, string username)
    {
        var userId = message.UserId;
        var isSlashCommand = command.StartsWith('/');

        switch (command)
        {
            case "/export":
                logger.LogDebug("Executing /export for {Username} ({UserId})", username, userId);
                await StartCommandSelectionAsync(userId, session, CommandGroup.Export);
                break;

            case "/status":
                logger.LogDebug("Executing /status for {Username} ({UserId})", username, userId);
                session.Reset(_options.RootPath);
                session.IsInStatusView = true;
                var sessionsStatus = await dataService.GetSessionsListAsync(userId);
                var keyboard = await keyboardBuilder.GetSessionsListKeyboardAsync(sessionsStatus);
                var statusMessage = await TrackMessageAsync(outputService.SendMessageWithKeyboardAsync(userId, "Сессии:", keyboard), session);
                session.StatusMessageId = statusMessage?.Id;
                break;

            case "/automation":
                logger.LogDebug("Executing /automation for {Username} ({UserId})", username, userId);
                await StartCommandSelectionAsync(userId, session, CommandGroup.Automation);
                break;

            case "/help":
                logger.LogDebug("Executing /help for {Username} ({UserId})", username, userId);
                session.Reset(_options.RootPath);
                await SendHelpMessageAsync(userId, session);
                break;

            default:
                if (isSlashCommand)
                {
                    logger.LogWarning("Unknown slash command '{Command}' from {Username} ({UserId})", command, username, userId);
                }
                else
                {
                    logger.LogDebug("Ignoring non-command text from {Username} ({UserId})", username, userId);
                }

                break;
        }
    }

    private async Task<bool> HandleCommandSelectionActionsAsync(
        long userId, string username, string messageText, UserSession session, CancellationToken cancellationToken)
    {
        if (messageText == ButtonTexts.Apply ||
            (messageText == ButtonTexts.Confirm && !session.IsFileSelectionActive && session.PendingCommand.Count > 0))
        {
            await ApplyCommandSelectionAsync(userId, username, session);
            return true;
        }

        if (messageText == ButtonTexts.Confirm && session.IsFileSelectionActive)
        {
            await ConfirmFileSelectionAsync(userId, username, session, cancellationToken);
            return true;
        }

        if (messageText == ButtonTexts.Cancel && (session.IsFileSelectionActive || session.PendingCommand.Count > 0))
        {
            logger.LogDebug("User {Username} ({UserId}) cancelled active selection", username, userId);
            session.Reset(_options.RootPath);
            await TrackMessageAsync(outputService.RemoveReplyKeyboardAsync(userId, "Выбор отменен."), session);
            return true;
        }

        return false;
    }

    private async Task ApplyCommandSelectionAsync(long userId, string username, UserSession session)
    {
        if (session.PendingCommand.Count == 0)
        {
            logger.LogDebug("User {Username} ({UserId}) tried to apply with no commands selected", username, userId);
            await SendWarningAndCleanupAsync(userId, session, "Сначала выберите хотя бы одну команду.");
            return;
        }

        logger.LogDebug("User {Username} ({UserId}) confirmed command selection: [{Commands}], opening file browser",
            username, userId, string.Join(", ", session.PendingCommand));

        await outputService.ClearChatHistoryAsync(userId, session);
        session.CommandSelectionMessageId = null;
        session.CurrentPath = _options.RootPath;
        session.IsFileSelectionActive = true;

        var keyboard = await keyboardBuilder.GetSelectionKeyboardAsync(userId, session);
        var selectionMessage = await TrackMessageAsync(outputService.SendMessageWithKeyboardAsync(userId, "Выберите папки:", keyboard), session);
        session.FileSelectionMessageId = selectionMessage?.Id;
        await SendFileActionsReplyKeyboardAsync(userId, session);
    }

    private async Task ConfirmFileSelectionAsync(long userId, string username, UserSession session, CancellationToken cancellationToken)
    {
        if (!session.FileSelectionMessageId.HasValue)
        {
            logger.LogWarning("Job submit blocked: user={UserId}, reason=missing_file_selection_message", userId);
            await SendWarningAndCleanupAsync(userId, session, "Сообщение выбора файлов не найдено.");
            return;
        }

        if (_options.IsAtProjectLevel(session.CurrentPath))
        {
            var selectedProject = session.GetSelectedFiles().FirstOrDefault();
            if (selectedProject == null)
            {
                logger.LogDebug("Project confirm blocked: user={UserId}, reason=no_project_selected", userId);
                await SendWarningAndCleanupAsync(userId, session, "⚠️ Сначала выберите проект.");
                return;
            }

            session.CurrentPath = Path.Combine(selectedProject, _options.ProjectDirectoryName);
            session.ClearSelectedFiles();

            logger.LogDebug("User {Username} ({UserId}) confirmed project '{Project}', navigated to 01_PROJECT",
                username, userId, Path.GetFileName(selectedProject));

            var keyboard = await keyboardBuilder.GetSelectionKeyboardAsync(userId, session);
            await outputService.EditMessageReplyMarkupAsync(userId, session.FileSelectionMessageId.Value, keyboard);
            await SendFileActionsReplyKeyboardAsync(userId, session);
            await CleanupCurrentViewAsync(userId, session);
            return;
        }

        var selectedSections = session.GetSelectedFiles();
        if (selectedSections.Count == 0)
        {
            logger.LogDebug("Job submit blocked: user={UserId}, reason=no_sections_selected", userId);
            await SendWarningAndCleanupAsync(userId, session, "⚠️ Сначала выберите хотя бы один раздел.");
            return;
        }

        logger.LogDebug(
            "Job submit: user={UserId}, commands={CommandCount}, sections={SectionCount}",
            userId, session.PendingCommand.Count, selectedSections.Count);

        var commandNames = session.PendingCommandName;
        var projectName = GetCurrentProjectName(session);
        var sectionNames = selectedSections
            .Select(GetSafePathName)
            .ToArray();

        var filesToProcess = CollectRvtFiles(selectedSections, cancellationToken);
        if (filesToProcess.Count == 0)
        {
            logger.LogWarning("Job submit blocked: user={UserId}, reason=no_files_found", userId);
            await SendWarningAndCleanupAsync(userId, session, "⚠️ В выбранных разделах не найдены файлы для обработки.");
            return;
        }

        var queuedMessage = BuildJobQueuedMessage(commandNames, projectName, sectionNames, filesToProcess.Count);

        var sessionId = await dataService.CreateSessionWithCommandsAsync(
            session.PendingCommand, filesToProcess, userId, username, filesToProcess.Count);
        logger.LogInformation(
            "Job queued: session={SessionId}, user={UserId}, commands={CommandCount}, files={FileCount}",
            sessionId, userId, session.PendingCommand.Count, filesToProcess.Count);

        await dataService.NotifyNewCommandsAsync((int)sessionId);

        session.ResetNavigation(_options.RootPath);
        session.ClearPendingCommands();
        session.IsFileSelectionActive = false;

        await TrackMessageAsync(outputService.RemoveReplyKeyboardAsync(userId, queuedMessage), session);
    }

    private Task SendFileActionsReplyKeyboardAsync(long userId, UserSession session)
        => HandlerHelpers.SendActionsReplyKeyboardAsync(outputService, userId, session,
            _options.IsAtProjectLevel(session.CurrentPath)
                ? keyboardBuilder.GetProjectActionsReplyKeyboardAsync
                : keyboardBuilder.GetSectionActionsReplyKeyboardAsync);

    private async Task StartCommandSelectionAsync(long userId, UserSession session, CommandGroup commandGroup)
    {
        session.Reset(_options.RootPath);
        session.IsFileSelectionActive = false;

        var commandKeyboard = await keyboardBuilder.GetCommandKeyboardAsync(commandGroup, session);

        var replyKeyboard = await keyboardBuilder.GetCommandActionsReplyKeyboardAsync();

        var commandSelectionMessage = await TrackMessageAsync(outputService.SendMessageWithKeyboardAsync(userId, "Выберите команду:", commandKeyboard), session);
        session.CommandSelectionMessageId = commandSelectionMessage?.Id;
        var actionsMessage = await TrackMessageAsync(outputService.SendMessageWithReplyKeyboardAsync(userId, "Подтвердите выбор:", replyKeyboard), session);
        session.LastActionsMessageId = actionsMessage?.Id;
    }

    private async Task SendRegistrationMessageAsync(long userId, UserSession session)
    {
        var keyboard = new InlineKeyboardMarkup(
        [
            [
                InlineKeyboardButton.WithCallbackData("Запросить доступ", CallbackPrefixes.RequestAccess)
            ]
        ]);
        await TrackMessageAsync(outputService.SendMessageWithKeyboardAsync(userId,
            "Добро пожаловать!\n\nУ вас нет доступа к этому боту. Нажмите кнопку ниже, чтобы запросить доступ.",
            keyboard), session);
    }

    private async Task SendHelpMessageAsync(long userId, UserSession session)
    {
        var helpText = new StringBuilder()
            .AppendLine("*Доступные команды:*\n")
            .AppendLine("/export — экспорт файлов в PDF, DWG, NWC, IFC")
            .AppendLine("/automation — автоматизация задач связанными с BIM")
            .AppendLine("/status — статус выполнения задач и управление сессиями")
            .AppendLine("/help — справка по командам")
            .ToString();

        await TrackMessageAsync(outputService.SendMessageAsync(userId, helpText), session);
    }

    private async Task<Message?> TrackMessageAsync(Task<Message?> task, UserSession session)
    {
        var msg = await task;
        if (msg != null)
        {
            session.TrackMessage(msg.Id);
        }
        return msg;
    }

    private async Task SendWarningAndCleanupAsync(long userId, UserSession session, string message)
    {
        Message? warning;

        if (session.IsFileSelectionActive)
        {
            var replyKeyboard = _options.IsAtProjectLevel(session.CurrentPath)
                ? await keyboardBuilder.GetProjectActionsReplyKeyboardAsync()
                : await keyboardBuilder.GetSectionActionsReplyKeyboardAsync();
            warning = await TrackMessageAsync(outputService.SendMessageWithReplyKeyboardAsync(userId, message, replyKeyboard), session);
        }
        else if (session.CommandSelectionMessageId.HasValue || session.PendingCommand.Count > 0)
        {
            var replyKeyboard = await keyboardBuilder.GetCommandActionsReplyKeyboardAsync();
            warning = await TrackMessageAsync(outputService.SendMessageWithReplyKeyboardAsync(userId, message, replyKeyboard), session);
        }
        else
        {
            warning = await TrackMessageAsync(outputService.SendMessageAsync(userId, message), session);
        }

        await CleanupCurrentViewAsync(userId, session, warning?.Id);
    }

    private async Task CleanupCurrentViewAsync(long userId, UserSession session, params int?[] extraKeepMessageIds)
    {
        var keepMessageIds = new[]
            {
                session.CommandSelectionMessageId,
                session.FileSelectionMessageId,
                session.StatusMessageId,
                session.LastActionsMessageId
            }
            .Concat(extraKeepMessageIds)
            .Where(messageId => messageId.HasValue)
            .Select(messageId => messageId!.Value);

        await outputService.CleanupTrackedMessagesAsync(userId, session, keepMessageIds);
    }

    private static string NormalizeCommandText(string text)
    {
        if (!text.StartsWith('/'))
        {
            return text;
        }

        var mentionIndex = text.IndexOf('@');
        if (mentionIndex > 0)
        {
            text = text[..mentionIndex];
        }

        return text.ToLowerInvariant();
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
            builder.AppendLine($"• {MarkdownHelper.EscapeMarkdown(commandName)}");
        }

        builder
            .AppendLine()
            .AppendLine("📌 *Проект*")
            .AppendLine($"`{MarkdownHelper.EscapeMarkdown(projectName)}`")
            .AppendLine()
            .AppendLine("📂 *Разделы*");

        foreach (var sectionName in sectionNames)
        {
            builder.AppendLine($"• {MarkdownHelper.EscapeMarkdown(sectionName)}");
        }

        builder
            .AppendLine()
            .AppendLine($"📄 *Количество файлов:* `{fileCount}`");

        return builder.ToString();
    }

    private static string GetCurrentProjectName(UserSession session)
    {
        var projectDirectory = Directory.GetParent(session.CurrentPath);
        return projectDirectory == null
            ? GetSafePathName(session.CurrentPath)
            : GetSafePathName(projectDirectory.FullName);
    }

    private static string GetSafePathName(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private List<string> CollectRvtFiles(IReadOnlySet<string> sectionPaths, CancellationToken cancellationToken)
    {
        var files = new List<string>();

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
                    files.Add(file);
                }
            }
        }

        return files;
    }
}
