
using Microsoft.Extensions.Options;
using System.Text;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.DTOs;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;
using TelegramBot.Server.Helpers;
using TelegramBot.Server.Interfaces;
using TelegramBot.Server.Models;
using Message = Telegram.Bot.Types.Message;

namespace TelegramBot.Server.Services.Application;

public sealed class SlashCommandService(
    IDataService dataService,
    ITelegramOutputService outputService,
    IKeyboardBuilder keyboardBuilder,
    IOptions<FileSystemOptions> fileSystemOptions,
    ILogger<SlashCommandService> logger) : ISlashCommandService
{
    private readonly IDataService _dataService = dataService;
    private readonly ITelegramOutputService _outputService = outputService;
    private readonly IKeyboardBuilder _keyboardBuilder = keyboardBuilder;
    private readonly FileSystemOptions _options = fileSystemOptions.Value;

    public async Task HandleUserCommandAsync(MessageDto message, UserSession session, CancellationToken cancellationToken = default)
    {
        var userId = message.UserId;
        var rawText = message.Text!;
        var username = message.Username!;

        var text = NormalizeCommandText(rawText);

        ArgumentNullException.ThrowIfNullOrWhiteSpace(username);

        logger.LogInformation("Command received: command={Command}, user={UserId}", text, userId);

        if (text == "/start")
        {
            await _outputService.ClearChatHistoryAsync(userId, session);
            session.Reset(_options.RootPath);
            var user = await _dataService.GetUserAsync(userId);

            if (user?.Status != UserAccessStatus.Approved)
            {
                var adminUser = await _dataService.GetUserAsync(userId);
                if (adminUser?.Role == UserRole.Admin && adminUser.Status == UserAccessStatus.Approved)
                {
                    var now = DateTime.UtcNow;
                    await _dataService.UpsertUserAsync(new BotUser
                    {
                        UserId = userId,
                        Username = username,
                        Role = UserRole.Admin,
                        Status = UserAccessStatus.Approved,
                        CreatedAt = user?.CreatedAt ?? now,
                        UpdatedAt = now
                    });
                    user = await _dataService.GetUserAsync(userId);
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

        var userRecord = await _dataService.GetUserAsync(userId);
        if (userRecord?.Status != UserAccessStatus.Approved)
        {
            logger.LogWarning("Command rejected: command={Command}, user={UserId}, reason=access_denied", text, userId);
            _ = await TrackMessageAsync(_outputService.SendMessageAsync(userId, "У вас нет доступа. Введите /start для запроса доступа."), session);
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
        var userRecord = await _dataService.GetUserAsync(userId);

        if (userRecord?.Status == UserAccessStatus.Approved)
        {
            return true;
        }

        _ = await TrackMessageAsync(_outputService.SendMessageAsync(userId, "У вас нет доступа. Введите /start для запроса доступа."), session);
        return false;
    }

    private async Task HandleSlashCommandAsync(string command, MessageDto message, UserSession session, string username)
    {
        var userId = message.UserId;
        var isSlashCommand = command.StartsWith('/');

        if (isSlashCommand)
        {
            await _outputService.ClearChatHistoryAsync(userId, session);
        }

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
                var sessionsStatus = await _dataService.GetSessionsListAsync(userId);
                var keyboard = await _keyboardBuilder.GetSessionsListKeyboardAsync(sessionsStatus);
                var statusMessage = await TrackMessageAsync(_outputService.SendMessageWithKeyboardAsync(userId, "Сессии:", keyboard), session);
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

        if (messageText == ButtonTexts.Back)
        {
            if (session.IsFileSelectionActive)
            {
                await BackInFileSelectionAsync(userId, username, session);
                return true;
            }

            if (session.StatusMessageId.HasValue)
            {
                await BackToSessionsListAsync(userId, username, session);
                return true;
            }
        }

        if (messageText == ButtonTexts.Cancel && (session.IsFileSelectionActive || session.PendingCommand.Count > 0))
        {
            logger.LogDebug("User {Username} ({UserId}) cancelled active selection", username, userId);
            await _outputService.ClearChatHistoryAsync(userId, session);
            session.Reset(_options.RootPath);
            _ = await TrackMessageAsync(_outputService.RemoveReplyKeyboardAsync(userId, "Выбор отменен."), session);
            return true;
        }

        return false;
    }

    private async Task ApplyCommandSelectionAsync(long userId, string username, UserSession session)
    {
        if (session.PendingCommand.Count == 0)
        {
            logger.LogDebug("User {Username} ({UserId}) tried to apply with no commands selected", username, userId);
            _ = await TrackMessageAsync(_outputService.SendMessageAsync(userId, "Сначала выберите хотя бы одну команду."), session);
            return;
        }

        logger.LogInformation("User {Username} ({UserId}) confirmed command selection: [{Commands}], opening file browser",
            username, userId, string.Join(", ", session.PendingCommand));

        await _outputService.ClearChatHistoryAsync(userId, session);
        session.CurrentPath = _options.RootPath;
        session.IsFileSelectionActive = true;

        var keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);
        var selectionMessage = await TrackMessageAsync(_outputService.SendMessageWithKeyboardAsync(userId, "Выберите папки:", keyboard), session);
        session.FileSelectionMessageId = selectionMessage?.Id;
        await SendFileActionsReplyKeyboardAsync(userId, session);
    }

    private async Task ConfirmFileSelectionAsync(long userId, string username, UserSession session, CancellationToken cancellationToken)
    {
        if (!session.FileSelectionMessageId.HasValue)
        {
            logger.LogWarning("Job submit blocked: user={UserId}, reason=missing_file_selection_message", userId);
            _ = await TrackMessageAsync(_outputService.SendMessageAsync(userId, "Сообщение выбора файлов не найдено."), session);
            return;
        }

        if (_options.IsAtProjectLevel(session.CurrentPath))
        {
            var selectedProject = session.SelectedFiles.FirstOrDefault();
            if (selectedProject == null)
            {
                logger.LogDebug("Project confirm blocked: user={UserId}, reason=no_project_selected", userId);
                _ = await TrackMessageAsync(_outputService.SendMessageAsync(userId, "Сначала выберите проект."), session);
                return;
            }

            session.CurrentPath = Path.Combine(selectedProject, _options.ProjectDirectoryName);
            session.ClearSelectedFiles();

            logger.LogInformation("User {Username} ({UserId}) confirmed project '{Project}', navigated to 01_PROJECT",
                username, userId, Path.GetFileName(selectedProject));

            var keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);
            await _outputService.EditMessageReplyMarkupAsync(userId, session.FileSelectionMessageId.Value, keyboard);
            await SendFileActionsReplyKeyboardAsync(userId, session);
            return;
        }

        var selectedSections = session.SelectedFiles;
        if (selectedSections.Count == 0)
        {
            logger.LogDebug("Job submit blocked: user={UserId}, reason=no_sections_selected", userId);
            _ = await TrackMessageAsync(_outputService.SendMessageAsync(userId, "Сначала выберите хотя бы один раздел."), session);
            return;
        }

        logger.LogInformation(
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
            logger.LogWarning("Job submit: user={UserId}, commands={CommandCount}, files=0", userId, session.PendingCommand.Count);
        }

        var queuedMessage = BuildJobQueuedMessage(commandNames, projectName, sectionNames, filesToProcess.Count);

        var sessionId = await _dataService.CreateSessionWithCommandsAsync(
            session.PendingCommand, filesToProcess, userId, username, filesToProcess.Count);
        logger.LogInformation(
            "Job queued: session={SessionId}, user={UserId}, commands={CommandCount}, files={FileCount}",
            sessionId, userId, session.PendingCommand.Count, filesToProcess.Count);

        await _dataService.NotifyNewCommandsAsync((int)sessionId);

        await _outputService.ClearChatHistoryAsync(userId, session);

        session.ResetNavigation(_options.RootPath);
        session.ClearPendingCommands();
        session.IsFileSelectionActive = false;

        _ = await TrackMessageAsync(_outputService.RemoveReplyKeyboardAsync(userId, queuedMessage), session);
    }

    private async Task BackInFileSelectionAsync(long userId, string username, UserSession session)
    {
        if (!session.FileSelectionMessageId.HasValue)
        {
            _ = await TrackMessageAsync(_outputService.SendMessageAsync(userId, "Сообщение выбора файлов не найдено."), session);
            return;
        }

        if (_options.IsAtProjectLevel(session.CurrentPath))
        {
            _ = await TrackMessageAsync(_outputService.SendMessageAsync(userId, "Вы уже в списке проектов."), session);
            return;
        }

        session.ClearSelectedFiles();
        session.CurrentPath = _options.RootPath;

        logger.LogInformation("User {Username} ({UserId}) returned to project selection", username, userId);

        var keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);
        await _outputService.EditMessageReplyMarkupAsync(userId, session.FileSelectionMessageId.Value, keyboard);
        await SendFileActionsReplyKeyboardAsync(userId, session);
    }

    private async Task BackToSessionsListAsync(long userId, string username, UserSession session)
    {
        if (!session.StatusMessageId.HasValue)
        {
            return;
        }

        logger.LogInformation("User {Username} ({UserId}) returning to sessions list", username, userId);

        var sessionsStatus = await _dataService.GetSessionsListAsync(userId);
        var keyboard = await _keyboardBuilder.GetSessionsListKeyboardAsync(sessionsStatus);
        await _outputService.EditMessageTextWithKeyboardAsync(userId, session.StatusMessageId.Value, "Сессии:", keyboard);

        session.IsInStatusView = true;
        _ = await TrackMessageAsync(_outputService.RemoveReplyKeyboardAsync(userId, "Сессии:"), session);
    }

    private async Task SendFileActionsReplyKeyboardAsync(long userId, UserSession session)
    {
        var replyKeyboard = _options.IsAtProjectLevel(session.CurrentPath)
            ? await _keyboardBuilder.GetProjectActionsReplyKeyboardAsync()
            : await _keyboardBuilder.GetSectionActionsReplyKeyboardAsync();

        _ = await TrackMessageAsync(_outputService.SendMessageWithReplyKeyboardAsync(userId, "Действия:", replyKeyboard), session);
    }

    private async Task StartCommandSelectionAsync(long userId, UserSession session, CommandGroup commandGroup)
    {
        session.Reset(_options.RootPath);
        session.IsFileSelectionActive = false;

        var commandKeyboard = await _keyboardBuilder.GetCommandKeyboardAsync(commandGroup, session);

        var replyKeyboard = await _keyboardBuilder.GetCommandActionsReplyKeyboardAsync();

        var commandSelectionMessage = await TrackMessageAsync(_outputService.SendMessageWithKeyboardAsync(userId, "Выберите команду:", commandKeyboard), session);
        session.CommandSelectionMessageId = commandSelectionMessage?.Id;
        _=await TrackMessageAsync(_outputService.SendMessageWithReplyKeyboardAsync(userId, "Подтвердите выбор:", replyKeyboard), session);
    }

    private async Task SendRegistrationMessageAsync(long userId, UserSession session)
    {
        var keyboard = new Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup([[
            Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData("Запросить доступ", CallbackPrefixes.RequestAccess)
        ]]);
        _ = await TrackMessageAsync(_outputService.SendMessageWithKeyboardAsync(userId,
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

        _ = await TrackMessageAsync(_outputService.SendMessageAsync(userId, helpText), session);
    }

    private static async Task<Message?> TrackMessageAsync(Task<Message?> task, UserSession session)
    {
        var msg = await task;
        if (msg != null)
        {
            session.TrackMessage(msg.Id);
        }

        return msg;
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
            .AppendLine("🧰 *Команды*");

        foreach (var commandName in commandNames)
        {
            _ = builder.AppendLine($"• {MarkdownHelper.EscapeMarkdown(commandName)}");
        }

        _ = builder
            .AppendLine()
            .AppendLine("📌 *Проект*")
            .AppendLine(MarkdownHelper.EscapeMarkdown(projectName))
            .AppendLine()
            .AppendLine("📂 *Разделы*");

        foreach (var sectionName in sectionNames)
        {
            _ = builder.AppendLine($"• {MarkdownHelper.EscapeMarkdown(sectionName)}");
        }

        _ = builder
            .AppendLine()
            .AppendLine($"📄 *Количество файлов:* {fileCount}");

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
