using System.Text;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotServer.Config;
using TelegramBotServer.DTOs;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;
using TelegramBotServer.Services.Application;
using TelegramBotServer.Services.Infrastructure.Telegram;

namespace TelegramBotServer.Services;

/// <summary>
/// Main application service for handling user commands and callbacks.
/// Delegates callback processing to specialized handlers via CallbackDispatcher.
/// </summary>
public sealed class CommandAppService : ICommandAppService
{
    private readonly IDataService _dataService;
    private readonly ITelegramOutputService _outputService;
    private readonly ISessionManager _sessionManager;
    private readonly IKeyboardBuilder _keyboardBuilder;
    private readonly CallbackDispatcher _callbackDispatcher;
    private readonly ILogger<CommandAppService> _logger;
    private readonly FileSystemOptions _options;

    public CommandAppService(
        IDataService dataService,
        ITelegramOutputService outputService,
        ISessionManager sessionManager,
        IKeyboardBuilder keyboardBuilder,
        CallbackDispatcher callbackDispatcher,
        IOptions<FileSystemOptions> fileSystemOptions,
        ILogger<CommandAppService> logger)
    {
        _dataService = dataService;
        _outputService = outputService;
        _sessionManager = sessionManager;
        _keyboardBuilder = keyboardBuilder;
        _callbackDispatcher = callbackDispatcher;
        _logger = logger;
        _options = fileSystemOptions.Value;
    }

    /// <summary>
    /// Обрабатывает входящее текстовое сообщение от пользователя.
    /// </summary>
    public async Task HandleUserCommandAsync(MessageDto message, CancellationToken cancellationToken = default)
    {
        // Username и Text уже проверены в TelegramBotHostedService
        string rawText = message.Text!;
        string text = NormalizeCommandText(rawText);
        long userId = message.UserId;
        string username = message.Username ?? string.Empty;

        _logger.LogInformation("Received command '{Command}' from {Username} ({UserId})", rawText, username, userId);

        var session = _sessionManager.GetOrCreateSession(userId);

        if (await HandleReplyKeyboardActionAsync(userId, username, rawText, session, cancellationToken))
        {
            return;
        }

        await HandleSlashCommandAsync(text, message, session, username, cancellationToken);
    }

    /// <summary>
    /// Обрабатывает callback-запрос от inline-кнопки.
    /// </summary>
    public async Task HandleCallbackAsync(CallbackQueryDto callback, CancellationToken cancellationToken = default)
    {
        if (callback.Username == null || callback.MessageText == null ||
            callback.CallbackData == null || callback.CallbackQueryId == null)
        {
            await _outputService.SendMessageAsync(callback.UserId, "Callback is empty.");
            return;
        }

        var session = _sessionManager.GetOrCreateSession(callback.UserId);

        _logger.LogInformation("Received callback '{Prefix}' from {Username} ({UserId})",
            CallbackDataParser.Parse(callback.CallbackData).Prefix, callback.Username, callback.UserId);

        var context = new CallbackContext
        {
            UserId = callback.UserId,
            ChatId = callback.ChatId,
            MessageId = callback.MessageId,
            Username = callback.Username,
            CallbackQueryId = callback.CallbackQueryId,
            ParsedCallback = CallbackDataParser.Parse(callback.CallbackData),
            Session = session,
            Buttons = callback.Buttons
        };

        await _callbackDispatcher.DispatchAsync(context, cancellationToken);
    }

    // ========== Private helper methods ==========

    private async Task StartCommandSelectionAsync(
        long userId,
        UserSession session,
        bool isAutomation,
        CancellationToken cancellationToken)
    {
        session.Reset(_options.RootPath);
        session.IsFileSelectionActive = false;

        var commandKeyboard = isAutomation
            ? await _keyboardBuilder.GetAutomationKeyboardAsync(session)
            : await _keyboardBuilder.GetCommandsKeyboardAsync(session);

        var commandMsg = await _outputService.SendMessageWithKeyboardAsync(userId, "Выберите команду:", commandKeyboard);
        if (commandMsg != null) session.AddBotMessageId(commandMsg.Id);

        var replyKeyboard = isAutomation
            ? await _keyboardBuilder.GetAutomationActionsReplyKeyboardAsync()
            : await _keyboardBuilder.GetExportActionsReplyKeyboardAsync();

        var replyMsg = await _outputService.SendMessageWithReplyKeyboardAsync(userId, "Подтвердите выбор:", replyKeyboard);
        if (replyMsg != null) session.AddBotMessageId(replyMsg.Id);
    }

    private async Task HandleSlashCommandAsync(
        string command,
        MessageDto message,
        UserSession session,
        string username,
        CancellationToken cancellationToken)
    {
        long userId = message.UserId;
        bool isSlashCommand = command.StartsWith('/');

        if (isSlashCommand)
            await DeletePreviousMessagesAsync(userId, session);

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

                var sessionsStatus = await _dataService.GetSessionsListAsync(userId);
                var keyboard = await _keyboardBuilder.GetSessionsListKeyboardAsync(sessionsStatus);
                var statusMsg = await _outputService.SendMessageWithKeyboardAsync(userId, "Сессии:", keyboard);
                if (statusMsg != null) session.AddBotMessageId(statusMsg.Id);
                break;

            case "/automation":
                _logger.LogDebug("Executing /automation for {Username} ({UserId})", username, userId);
                await StartCommandSelectionAsync(userId, session, isAutomation: true, cancellationToken);
                break;

            case "/start":
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

    private async Task DeletePreviousMessagesAsync(long userId, UserSession session)
    {
        var messageIds = session.TakeAllBotMessageIds();
        foreach (var messageId in messageIds)
            await _outputService.DeleteMessageAsync(userId, messageId);
    }

    private async Task<bool> HandleReplyKeyboardActionAsync(
        long userId,
        string username,
        string messageText,
        UserSession session,
        CancellationToken cancellationToken)
    {
        if (session.IsFileSelectionActive)
        {
            if (messageText == ButtonTexts.ExportApply || messageText == ButtonTexts.AutomationApply)
            {
                if (session.SelectedFiles.Count == 0)
                {
                    _logger.LogDebug("User {Username} ({UserId}) tried to apply with no files selected", username, userId);
                    await _outputService.SendMessageAsync(userId, "Сначала выберите хотя бы один файл.");
                    return true;
                }

                _logger.LogInformation("User {Username} ({UserId}) applying file selection: {FileCount} files selected",
                    username, userId, session.SelectedFiles.Count);
                await DispatchFileSelectionCallbackAsync(userId, username, session, CallbackPrefixes.ApplyFiles, cancellationToken);
                session.IsFileSelectionActive = false;
                var applyMsg = await _outputService.RemoveReplyKeyboardAsync(userId, "Выбор файлов подтвержден.");
                if (applyMsg != null) session.AddBotMessageId(applyMsg.Id);
                return true;
            }

            if (messageText == ButtonTexts.Cancel)
            {
                _logger.LogDebug("User {Username} ({UserId}) cancelling file selection", username, userId);
                await DispatchFileSelectionCallbackAsync(userId, username, session, CallbackPrefixes.CancelFileSelection, cancellationToken);
                session.IsFileSelectionActive = false;

                var cancelFileMsg = await _outputService.RemoveReplyKeyboardAsync(userId, "Выбор файлов отменен.");
                if (cancelFileMsg != null) session.AddBotMessageId(cancelFileMsg.Id);

                var replyKeyboard = session.ContainsPendingCommand(CommandCodes.BimDoc)
                    || session.ContainsPendingCommand(CommandCodes.ClashRep)
                    || session.ContainsPendingCommand(CommandCodes.AutoRes)
                    ? await _keyboardBuilder.GetAutomationActionsReplyKeyboardAsync()
                    : await _keyboardBuilder.GetExportActionsReplyKeyboardAsync();

                var backToSelMsg = await _outputService.SendMessageWithReplyKeyboardAsync(userId, "Подтвердите выбор:", replyKeyboard);
                if (backToSelMsg != null) session.AddBotMessageId(backToSelMsg.Id);
                return true;
            }

            return false;
        }

        if (messageText == ButtonTexts.ExportApply || messageText == ButtonTexts.AutomationApply)
        {
            if (session.PendingCommand.Count == 0)
            {
                _logger.LogDebug("User {Username} ({UserId}) tried to apply with no commands selected", username, userId);
                await _outputService.SendMessageAsync(userId, "Сначала выберите хотя бы одну команду.");
                return true;
            }

            _logger.LogInformation("User {Username} ({UserId}) confirmed command selection: [{Commands}], opening file browser",
                username, userId, string.Join(", ", session.PendingCommand));
            session.CurrentPath = _options.RootPath;
            var keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);
            var selectionMessage = await _outputService.SendMessageWithKeyboardAsync(userId, "Выберите файлы:", keyboard);
            session.FileSelectionMessageId = selectionMessage?.Id;
            if (selectionMessage != null) session.AddBotMessageId(selectionMessage.Id);
            session.IsFileSelectionActive = true;

            var fileActionsReplyKeyboard = await _keyboardBuilder.GetFileActionsReplyKeyboardAsync(session);
            var fileActionsMsg = await _outputService.SendMessageWithReplyKeyboardAsync(userId, "Действия с файлами:", fileActionsReplyKeyboard);
            if (fileActionsMsg != null) session.AddBotMessageId(fileActionsMsg.Id);
            return true;
        }

        if (messageText == ButtonTexts.Cancel)
        {
            session.ClearPendingCommands();
            session.IsFileSelectionActive = false;
            _logger.LogDebug("User {Username} ({UserId}) cancelled command selection", username, userId);
            var cancelCmdMsg = await _outputService.RemoveReplyKeyboardAsync(userId, "Выбор команд отменен.");
            if (cancelCmdMsg != null) session.AddBotMessageId(cancelCmdMsg.Id);
            return true;
        }

        return false;
    }

    private async Task SendHelpMessageAsync(long userId)
    {
        var helpText = new StringBuilder()
            .AppendLine("*Доступные команды:*\n")
            .AppendLine("▪️ /export — экспорт файлов в PDF, DWG, NWC, IFC")
            .AppendLine("▪️ /automation — автоматизация задач связанными с BIM")
            .AppendLine("▪️ /status — статус выполнения задач и управление сессиями")
            .AppendLine("▪️ /help — справка по командам")
            .ToString();

        await _outputService.SendMessageAsync(userId, helpText);
    }

    private async Task DispatchFileSelectionCallbackAsync(
        long userId,
        string username,
        UserSession session,
        string callbackPrefix,
        CancellationToken cancellationToken)
    {
        if (session.FileSelectionMessageId is not int messageId)
        {
            var keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);
            var selectionMessage = await _outputService.SendMessageWithKeyboardAsync(userId, "Выберите файлы:", keyboard);
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

        await _callbackDispatcher.DispatchAsync(context, cancellationToken);
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
