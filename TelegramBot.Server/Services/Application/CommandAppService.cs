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

/// <summary>
/// Main application service for handling user commands and callbacks.
/// Delegates callback processing to specialized handlers via ICallbackDispatcher.
/// </summary>
public sealed class CommandAppService(
    IDataService dataService,
    ITelegramOutputService outputService,
    ISessionManager sessionManager,
    IKeyboardBuilder keyboardBuilder,
    ICallbackDispatcher callbackDispatcher,
    IOptions<FileSystemOptions> fileSystemOptions,
    ILogger<CommandAppService> logger) : ICommandAppService
{
    private readonly IDataService _dataService = dataService;
    private readonly ITelegramOutputService _outputService = outputService;
    private readonly ISessionManager _sessionManager = sessionManager;
    private readonly IKeyboardBuilder _keyboardBuilder = keyboardBuilder;
    private readonly ICallbackDispatcher _callbackDispatcher = callbackDispatcher;
    private readonly ILogger<CommandAppService> _logger = logger;
    private readonly FileSystemOptions _options = fileSystemOptions.Value;

    public async Task HandleUserCommandAsync(MessageDto message, CancellationToken cancellationToken = default)
    {
        long userId = message.UserId;
        string rawText = message.Text!;
        string username = message.Username!;

        string text = NormalizeCommandText(rawText);

        ArgumentNullException.ThrowIfNullOrWhiteSpace(username);

        _logger.LogInformation("Received command '{Command}' from {Username} ({UserId})", rawText, username, userId);

        UserSession session = _sessionManager.GetOrCreateSession(userId);

        // /start available to everyone regardless of access status
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
            {
                await SendHelpMessageAsync(userId);
            }
            else
            {
                await SendRegistrationMessageAsync(userId);
            }

            return;
        }

        BotUser? userRecord = await _dataService.GetUserAsync(userId);
        if (userRecord?.Status != UserAccessStatus.Approved)
        {
            _ = await _outputService.SendMessageAsync(userId, "У вас нет доступа. Введите /start для запроса доступа.");
            return;
        }

        if (await HandleReplyKeyboardActionAsync(userId, username, rawText, session, cancellationToken))
        {
            return;
        }

        await HandleSlashCommandAsync(text, message, session, username, cancellationToken);
    }

    public async Task HandleCallbackAsync(CallbackQueryDto callback, CancellationToken cancellationToken = default)
    {
        if (callback.Username == null || callback.MessageText == null ||  callback.CallbackData == null || callback.CallbackQueryId == null)
        {
            _ = await _outputService.SendMessageAsync(callback.UserId, "Callback is empty.");
            return;
        }

        UserSession session = _sessionManager.GetOrCreateSession(callback.UserId);
        ParsedCallback parsed = CallbackDataParser.Parse(callback.CallbackData);

        _logger.LogInformation("Received callback '{Prefix}' from {Username} ({UserId})", parsed.Prefix, callback.Username, callback.UserId);

        // Registration callbacks bypass access check
        bool isRegistrationCallback = parsed.Prefix is
            CallbackPrefixes.RequestAccess or
            CallbackPrefixes.ApproveUser or
            CallbackPrefixes.RejectUser;

        if (!isRegistrationCallback)
        {
            BotUser? userRecord = await _dataService.GetUserAsync(callback.UserId);
            if (userRecord?.Status != UserAccessStatus.Approved)
            {
                _ = await _outputService.SendMessageAsync(callback.UserId,
                    "У вас нет доступа. Введите /start для запроса доступа.");
                return;
            }
        }

        var context = new CallbackContext
        {
            UserId = callback.UserId,
            ChatId = callback.ChatId,
            MessageId = callback.MessageId,
            Username = callback.Username,
            CallbackQueryId = callback.CallbackQueryId,
            ParsedCallback = parsed,
            Session = session,
            Buttons = callback.Buttons
        };

        _ = await _callbackDispatcher.DispatchAsync(context, cancellationToken);
    }

    private async Task StartCommandSelectionAsync(long userId, UserSession session, bool isAutomation, CancellationToken cancellationToken)
    {
        session.Reset(_options.RootPath);
        session.IsFileSelectionActive = false;

        InlineKeyboardMarkup commandKeyboard = isAutomation
            ? await _keyboardBuilder.GetAutomationKeyboardAsync(session)
            : await _keyboardBuilder.GetCommandsKeyboardAsync(session);

        await TrackMessageAsync(_outputService.SendMessageWithKeyboardAsync(userId, "Выберите команду:", commandKeyboard), session);

        ReplyKeyboardMarkup replyKeyboard = isAutomation
            ? await _keyboardBuilder.GetAutomationActionsReplyKeyboardAsync()
            : await _keyboardBuilder.GetExportActionsReplyKeyboardAsync();

        await TrackMessageAsync(_outputService.SendMessageWithReplyKeyboardAsync(userId, "Подтвердите выбор:", replyKeyboard), session);
    }

    private async Task HandleSlashCommandAsync(
        string command, MessageDto message, UserSession session, string username, CancellationToken cancellationToken)
    {
        long userId = message.UserId;
        bool isSlashCommand = command.StartsWith('/');

        if (isSlashCommand)
        {
            await _outputService.ClearChatHistoryAsync(userId, session);
        }

        switch (command)
        {
            case "/export":
                _logger.LogDebug("Executing /export for {Username} ({UserId})", username, userId);
                await StartCommandSelectionAsync(userId, session, isAutomation: false, cancellationToken);
                break;

            case "/status":
                _logger.LogDebug("Executing /status for {Username} ({UserId})", username, userId);
                session.Reset(_options.RootPath);
                session.IsInStatusView = true;
                List<SessionsList> sessionsStatus = await _dataService.GetSessionsListAsync(userId);
                InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSessionsListKeyboardAsync(sessionsStatus);
                _ = await TrackMessageAsync(_outputService.SendMessageWithKeyboardAsync(userId, "Сессии:", keyboard), session);
                break;

            case "/automation":
                _logger.LogDebug("Executing /automation for {Username} ({UserId})", username, userId);
                await StartCommandSelectionAsync(userId, session, isAutomation: true, cancellationToken);
                break;

            case "/help":
                _logger.LogDebug("Executing /help for {Username} ({UserId})", username, userId);
                session.Reset(_options.RootPath);
                await SendHelpMessageAsync(userId);
                break;

            default:
                if (isSlashCommand)
                {
                    _logger.LogWarning("Unknown slash command '{Command}' from {Username} ({UserId})", command, username, userId);
                }
                else
                {
                    _logger.LogDebug("Ignoring non-command text from {Username} ({UserId})", username, userId);
                }

                break;
        }
    }

    private async Task<bool> HandleReplyKeyboardActionAsync(long userId, string username, string messageText, UserSession session, CancellationToken cancellationToken)
    {
        if (session.IsFileSelectionActive)
        {
            return await HandleFileSelectionActionsAsync(userId, username, messageText, session, cancellationToken);
        }

        return await HandleCommandSelectionActionsAsync(userId, username, messageText, session, cancellationToken);
    }

    private async Task<bool> HandleFileSelectionActionsAsync(long userId, string username, string messageText, UserSession session, CancellationToken cancellationToken)
    {
        if (messageText is ButtonTexts.ExportApply or ButtonTexts.AutomationApply)
        {
            if (session.SelectedFiles.Count == 0)
            {
                _logger.LogDebug("User {Username} ({UserId}) tried to apply with no files selected", username, userId);
                _ = await _outputService.SendMessageAsync(userId, "Сначала выберите хотя бы один файл.");
                return true;
            }

            _logger.LogInformation("User {Username} ({UserId}) applying file selection: {FileCount} files selected",
                username, userId, session.SelectedFiles.Count);
            await _outputService.ClearChatHistoryAsync(userId, session);
            await DispatchFileSelectionCallbackAsync(userId, username, session, CallbackPrefixes.ApplyFiles, cancellationToken);
            session.IsFileSelectionActive = false;
            _ = await TrackMessageAsync(_outputService.RemoveReplyKeyboardAsync(userId, "Выбор файлов подтвержден."), session);
            return true;
        }

        if (messageText == ButtonTexts.Cancel)
        {
            _logger.LogDebug("User {Username} ({UserId}) cancelling file selection", username, userId);
            await _outputService.ClearChatHistoryAsync(userId, session);
            await DispatchFileSelectionCallbackAsync(userId, username, session, CallbackPrefixes.CancelFileSelection, cancellationToken);
            session.IsFileSelectionActive = false;
            _ = await TrackMessageAsync(_outputService.RemoveReplyKeyboardAsync(userId, "Выбор файлов отменен."), session);

            ReplyKeyboardMarkup replyKeyboard = CommandCodes.AutomationCodes.Any(session.ContainsPendingCommand)
                ? await _keyboardBuilder.GetAutomationActionsReplyKeyboardAsync()
                : await _keyboardBuilder.GetExportActionsReplyKeyboardAsync();

            _ = await TrackMessageAsync(_outputService.SendMessageWithReplyKeyboardAsync(userId, "Подтвердите выбор:", replyKeyboard), session);
            return true;
        }

        return false;
    }

    private async Task<bool> HandleCommandSelectionActionsAsync(long userId, string username, string messageText, UserSession session, CancellationToken cancellationToken)
    {
        if (messageText is ButtonTexts.ExportApply or ButtonTexts.AutomationApply)
        {
            if (session.PendingCommand.Count == 0)
            {
                _logger.LogDebug("User {Username} ({UserId}) tried to apply with no commands selected", username, userId);
                _ = await _outputService.SendMessageAsync(userId, "Сначала выберите хотя бы одну команду.");
                return true;
            }

            _logger.LogInformation("User {Username} ({UserId}) confirmed command selection: [{Commands}], opening file browser",
                username, userId, string.Join(", ", session.PendingCommand));
            await _outputService.ClearChatHistoryAsync(userId, session);
            session.CurrentPath = _options.RootPath;
            InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);
            Message? selectionMessage = await TrackMessageAsync(_outputService.SendMessageWithKeyboardAsync(userId, "Выберите файлы:", keyboard), session);
            session.FileSelectionMessageId = selectionMessage?.Id;
            session.IsFileSelectionActive = true;

            ReplyKeyboardMarkup fileActionsReplyKeyboard = await _keyboardBuilder.GetFileActionsReplyKeyboardAsync(session);
            _ = await TrackMessageAsync(_outputService.SendMessageWithReplyKeyboardAsync(userId, "Действия с файлами:", fileActionsReplyKeyboard), session);
            return true;
        }

        if (messageText == ButtonTexts.Cancel)
        {
            session.ClearPendingCommands();
            session.IsFileSelectionActive = false;
            _logger.LogDebug("User {Username} ({UserId}) cancelled command selection", username, userId);
            await _outputService.ClearChatHistoryAsync(userId, session);
            _ = await TrackMessageAsync(_outputService.RemoveReplyKeyboardAsync(userId, "Выбор команд отменен."), session);
            return true;
        }

        return false;
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

    private async Task DispatchFileSelectionCallbackAsync(
        long userId, string username, UserSession session, string callbackPrefix, CancellationToken cancellationToken)
    {
        if (session.FileSelectionMessageId is not int messageId)
        {
            InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);
            Message? selectionMessage = await _outputService.SendMessageWithKeyboardAsync(userId, "Выберите файлы:", keyboard);
            session.FileSelectionMessageId = selectionMessage?.Id;
            return;
        }

        var context = new CallbackContext
        {
            UserId = userId,
            ChatId = userId,
            MessageId = messageId,
            Username = username,
            CallbackQueryId = string.Empty,
            ParsedCallback = new ParsedCallback(callbackPrefix, string.Empty),
            Session = session,
            Buttons = []
        };

        _ = await _callbackDispatcher.DispatchAsync(context, cancellationToken);
    }

    private async Task<Message?> TrackMessageAsync(Task<Message?> task, UserSession session)
    {
        Message? msg = await task;
        if (msg != null)
        {
            session.AddBotMessageId(msg.Id);
        }

        return msg;
    }

    private static string NormalizeCommandText(string text)
    {
        if (!text.StartsWith('/'))
        {
            return text;
        }

        int mentionIndex = text.IndexOf('@');
        if (mentionIndex > 0)
        {
            text = text[..mentionIndex];
        }

        return text.ToLowerInvariant();
    }
}
