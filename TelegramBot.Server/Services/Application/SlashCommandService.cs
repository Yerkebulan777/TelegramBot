#nullable enable

using Microsoft.Extensions.Options;
using System.Text;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.DTOs;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;
using TelegramBot.Server.Interfaces;

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
        long userId = message.UserId;
        string rawText = message.Text!;
        string username = message.Username!;

        string text = NormalizeCommandText(rawText);

        ArgumentNullException.ThrowIfNullOrWhiteSpace(username);

        logger.LogInformation("Received command '{Command}' from {Username} ({UserId})", rawText, username, userId);

        if (text == "/start")
        {
            session.Reset(_options.RootPath);
            await _outputService.ClearChatHistoryAsync(userId, session);
            BotUser? user = await _dataService.GetUserAsync(userId);

            if (user?.Status != UserAccessStatus.Approved)
            {
                BotUser? adminUser = await _dataService.GetBotUserAsync(userId);
                if (adminUser?.Role == UserRole.Admin && adminUser.Status == UserAccessStatus.Approved)
                {
                    DateTime now = DateTime.UtcNow;
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
                await SendHelpMessageAsync(userId);
            else
                await SendRegistrationMessageAsync(userId);

            return;
        }

        BotUser? userRecord = await _dataService.GetUserAsync(userId);
        if (userRecord?.Status != UserAccessStatus.Approved)
        {
            _ = await _outputService.SendMessageAsync(userId, "У вас нет доступа. Введите /start для запроса доступа.");
            return;
        }

        if (await HandleCommandSelectionActionsAsync(userId, username, rawText, session, cancellationToken))
            return;

        await HandleSlashCommandAsync(text, message, session, username, cancellationToken);
    }

    public async Task<bool> CheckAndNotifyAccessAsync(long userId)
    {
        BotUser? userRecord = await _dataService.GetUserAsync(userId);
        if (userRecord?.Status == UserAccessStatus.Approved)
            return true;

        _ = await _outputService.SendMessageAsync(userId, "У вас нет доступа. Введите /start для запроса доступа.");
        return false;
    }

    private async Task HandleSlashCommandAsync(
        string command, MessageDto message, UserSession session, string username, CancellationToken cancellationToken)
    {
        long userId = message.UserId;
        bool isSlashCommand = command.StartsWith('/');

        if (isSlashCommand)
            await _outputService.ClearChatHistoryAsync(userId, session);

        switch (command)
        {
            case "/export":
                logger.LogDebug("Executing /export for {Username} ({UserId})", username, userId);
                await StartCommandSelectionAsync(userId, session, isAutomation: false, cancellationToken);
                break;

            case "/status":
                logger.LogDebug("Executing /status for {Username} ({UserId})", username, userId);
                session.Reset(_options.RootPath);
                session.IsInStatusView = true;
                var sessionsStatus = await _dataService.GetSessionsListAsync(userId);
                InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSessionsListKeyboardAsync(sessionsStatus);
                Message? statusMessage = await TrackMessageAsync(_outputService.SendMessageWithKeyboardAsync(userId, "Сессии:", keyboard), session);
                session.StatusMessageId = statusMessage?.Id;
                break;

            case "/automation":
                logger.LogDebug("Executing /automation for {Username} ({UserId})", username, userId);
                await StartCommandSelectionAsync(userId, session, isAutomation: true, cancellationToken);
                break;

            case "/help":
                logger.LogDebug("Executing /help for {Username} ({UserId})", username, userId);
                session.Reset(_options.RootPath);
                await SendHelpMessageAsync(userId);
                break;

            default:
                if (isSlashCommand)
                    logger.LogWarning("Unknown slash command '{Command}' from {Username} ({UserId})", command, username, userId);
                else
                    logger.LogDebug("Ignoring non-command text from {Username} ({UserId})", username, userId);
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
            _ = await _outputService.SendMessageAsync(userId, "Сначала выберите хотя бы одну команду.");
            return;
        }

        logger.LogInformation("User {Username} ({UserId}) confirmed command selection: [{Commands}], opening file browser",
            username, userId, string.Join(", ", session.PendingCommand));

        await _outputService.ClearChatHistoryAsync(userId, session);
        session.CurrentPath = _options.RootPath;
        session.IsFileSelectionActive = true;

        InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);
        Message? selectionMessage = await TrackMessageAsync(_outputService.SendMessageWithKeyboardAsync(userId, "Выберите папки:", keyboard), session);
        session.FileSelectionMessageId = selectionMessage?.Id;
        await SendFileActionsReplyKeyboardAsync(userId, session);
    }

    private async Task ConfirmFileSelectionAsync(long userId, string username, UserSession session, CancellationToken cancellationToken)
    {
        if (!session.FileSelectionMessageId.HasValue)
        {
            _ = await _outputService.SendMessageAsync(userId, "Сообщение выбора файлов не найдено.");
            return;
        }

        if (IsAtProjectLevel(session))
        {
            string? selectedProject = session.SelectedFiles.FirstOrDefault();
            if (selectedProject == null)
            {
                _ = await _outputService.SendMessageAsync(userId, "Сначала выберите проект.");
                return;
            }

            session.CurrentPath = Path.Combine(selectedProject, _options.ProjectDirectoryName);
            session.ClearSelectedFiles();

            logger.LogInformation("User {Username} ({UserId}) confirmed project '{Project}', navigated to 01_PROJECT",
                username, userId, Path.GetFileName(selectedProject));

            InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);
            await _outputService.EditMessageReplyMarkupAsync(userId, session.FileSelectionMessageId.Value, keyboard);
            await SendFileActionsReplyKeyboardAsync(userId, session);
            return;
        }

        IReadOnlySet<string> selectedSections = session.SelectedFiles;
        if (selectedSections.Count == 0)
        {
            _ = await _outputService.SendMessageAsync(userId, "Сначала выберите хотя бы один раздел.");
            return;
        }

        logger.LogInformation(
            "User {Username} ({UserId}) submitting job: commands=[{Commands}], sections={Count}",
            username, userId, string.Join(", ", session.PendingCommand), selectedSections.Count);

        List<string> filesToProcess = CollectRvtFiles(selectedSections, cancellationToken);

        await _dataService.CreateSessionWithCommandsAsync(
            session.PendingCommand, filesToProcess, userId, username, filesToProcess.Count);

        string queueReply = BuildQueueReply(session);
        await _outputService.EditMessageReplyTextAsync(userId, session.FileSelectionMessageId.Value, queueReply);

        session.ResetNavigation(_options.RootPath);
        session.ClearPendingCommands();
        session.IsFileSelectionActive = false;

        _ = await TrackMessageAsync(_outputService.RemoveReplyKeyboardAsync(userId, "Задание добавлено в очередь."), session);
    }

    private async Task BackInFileSelectionAsync(long userId, string username, UserSession session)
    {
        if (!session.FileSelectionMessageId.HasValue)
        {
            _ = await _outputService.SendMessageAsync(userId, "Сообщение выбора файлов не найдено.");
            return;
        }

        if (IsAtProjectLevel(session))
        {
            _ = await _outputService.SendMessageAsync(userId, "Вы уже в списке проектов.");
            return;
        }

        session.ClearSelectedFiles();
        session.CurrentPath = _options.RootPath;

        logger.LogInformation("User {Username} ({UserId}) returned to project selection", username, userId);

        InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);
        await _outputService.EditMessageReplyMarkupAsync(userId, session.FileSelectionMessageId.Value, keyboard);
        await SendFileActionsReplyKeyboardAsync(userId, session);
    }

    private async Task BackToSessionsListAsync(long userId, string username, UserSession session)
    {
        if (!session.StatusMessageId.HasValue)
            return;

        logger.LogInformation("User {Username} ({UserId}) returning to sessions list", username, userId);

        var sessionsStatus = await _dataService.GetSessionsListAsync(userId);
        InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSessionsListKeyboardAsync(sessionsStatus);
        await _outputService.EditMessageTextWithKeyboardAsync(userId, session.StatusMessageId.Value, "Сессии:", keyboard);

        session.IsInStatusView = true;
        _ = await TrackMessageAsync(_outputService.RemoveReplyKeyboardAsync(userId, "Сессии:"), session);
    }

    private async Task SendFileActionsReplyKeyboardAsync(long userId, UserSession session)
    {
        ReplyKeyboardMarkup replyKeyboard = IsAtProjectLevel(session)
            ? await _keyboardBuilder.GetProjectActionsReplyKeyboardAsync()
            : await _keyboardBuilder.GetSectionActionsReplyKeyboardAsync();

        _ = await TrackMessageAsync(_outputService.SendMessageWithReplyKeyboardAsync(userId, "Действия:", replyKeyboard), session);
    }

    private async Task StartCommandSelectionAsync(long userId, UserSession session, bool isAutomation, CancellationToken cancellationToken)
    {
        session.Reset(_options.RootPath);
        session.IsFileSelectionActive = false;

        InlineKeyboardMarkup commandKeyboard = isAutomation
            ? await _keyboardBuilder.GetAutomationKeyboardAsync(session)
            : await _keyboardBuilder.GetCommandsKeyboardAsync(session);

        ReplyKeyboardMarkup replyKeyboard = await _keyboardBuilder.GetCommandActionsReplyKeyboardAsync();

        Message? commandSelectionMessage = await TrackMessageAsync(_outputService.SendMessageWithKeyboardAsync(userId, "Выберите команду:", commandKeyboard), session);
        session.CommandSelectionMessageId = commandSelectionMessage?.Id;
        await TrackMessageAsync(_outputService.SendMessageWithReplyKeyboardAsync(userId, "Подтвердите выбор:", replyKeyboard), session);
    }

    private async Task SendRegistrationMessageAsync(long userId)
    {
        var keyboard = new InlineKeyboardMarkup([[
            InlineKeyboardButton.WithCallbackData("Запросить доступ", CallbackPrefixes.RequestAccess)
        ]]);
        _ = await _outputService.SendMessageWithKeyboardAsync(userId,
            "Добро пожаловать!\n\nУ вас нет доступа к этому боту. Нажмите кнопку ниже, чтобы запросить доступ.",
            keyboard);
    }

    private async Task SendHelpMessageAsync(long userId)
    {
        var helpText = new StringBuilder()
            .AppendLine("*Доступные команды:*\n")
            .AppendLine("/export — экспорт файлов в PDF, DWG, NWC, IFC")
            .AppendLine("/automation — автоматизация задач связанными с BIM")
            .AppendLine("/status — статус выполнения задач и управление сессиями")
            .AppendLine("/help — справка по командам")
            .ToString();

        _ = await _outputService.SendMessageAsync(userId, helpText);
    }

    private static async Task<Message?> TrackMessageAsync(Task<Message?> task, UserSession session)
    {
        Message? msg = await task;
        if (msg != null)
            session.TrackMessage(msg.Id);
        return msg;
    }

    private static string NormalizeCommandText(string text)
    {
        if (!text.StartsWith('/'))
            return text;

        int mentionIndex = text.IndexOf('@');
        if (mentionIndex > 0)
            text = text[..mentionIndex];

        return text.ToLowerInvariant();
    }

    private bool IsAtProjectLevel(UserSession session) =>
        !string.Equals(Path.GetFileName(session.CurrentPath), _options.ProjectDirectoryName,
            StringComparison.OrdinalIgnoreCase);

    private List<string> CollectRvtFiles(IReadOnlySet<string> sectionPaths, CancellationToken cancellationToken)
    {
        var files = new List<string>();

        foreach (string sectionPath in sectionPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string rvtDir = _options.GetRvtPath(sectionPath);
            if (!Directory.Exists(rvtDir))
            {
                logger.LogWarning("RVT directory not found: {RvtDir}", rvtDir);
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(rvtDir))
            {
                if (_options.IsRevitFile(file))
                    files.Add(file);
            }
        }

        return files;
    }

    private static string BuildQueueReply(UserSession session)
    {
        var sb = new StringBuilder("Команда:\n");
        foreach (string cmd in session.PendingCommandName)
            sb.Append("✅ ").Append(cmd).Append('\n');

        sb.Append("Добавлены файлы:\n");
        foreach (string file in session.SelectedFiles)
            sb.Append("✅ ").Append(Path.GetFileName(file)).Append('\n');

        sb.Append("\n/status для проверки статуса команды");
        return sb.ToString();
    }
}
