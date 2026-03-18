using System.Text;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;
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

        await TrackMessageAsync(_outputService.SendMessageWithKeyboardAsync(userId, "Выберите команду:", commandKeyboard), session);

        var replyKeyboard = isAutomation
            ? await _keyboardBuilder.GetAutomationActionsReplyKeyboardAsync()
            : await _keyboardBuilder.GetExportActionsReplyKeyboardAsync();

        await TrackMessageAsync(_outputService.SendMessageWithReplyKeyboardAsync(userId, "Подтвердите выбор:", replyKeyboard), session);
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
            await _outputService.ClearChatHistoryAsync(userId, session);

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
                await TrackMessageAsync(_outputService.SendMessageWithKeyboardAsync(userId, "Сессии:", keyboard), session);
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
                await TrackMessageAsync(_outputService.RemoveReplyKeyboardAsync(userId, "Выбор файлов подтвержден."), session);
                return true;
            }

            if (messageText == ButtonTexts.Cancel)
            {
                _logger.LogDebug("User {Username} ({UserId}) cancelling file selection", username, userId);
                await DispatchFileSelectionCallbackAsync(userId, username, session, CallbackPrefixes.CancelFileSelection, cancellationToken);
                session.IsFileSelectionActive = false;

                await TrackMessageAsync(_outputService.RemoveReplyKeyboardAsync(userId, "Выбор файлов отменен."), session);

                var replyKeyboard = CommandCodes.AutomationCodes.Any(session.ContainsPendingCommand)
                    ? await _keyboardBuilder.GetAutomationActionsReplyKeyboardAsync()
                    : await _keyboardBuilder.GetExportActionsReplyKeyboardAsync();

                await TrackMessageAsync(_outputService.SendMessageWithReplyKeyboardAsync(userId, "Подтвердите выбор:", replyKeyboard), session);
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
            await _outputService.ClearChatHistoryAsync(userId, session);
            session.CurrentPath = _options.RootPath;
            var keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);
            var selectionMessage = await TrackMessageAsync(_outputService.SendMessageWithKeyboardAsync(userId, "Выберите файлы:", keyboard), session);
            session.FileSelectionMessageId = selectionMessage?.Id;
            session.IsFileSelectionActive = true;

            var fileActionsReplyKeyboard = await _keyboardBuilder.GetFileActionsReplyKeyboardAsync(session);
            await TrackMessageAsync(_outputService.SendMessageWithReplyKeyboardAsync(userId, "Действия с файлами:", fileActionsReplyKeyboard), session);
            return true;
        }

        if (messageText == ButtonTexts.Cancel)
        {
            session.ClearPendingCommands();
            session.IsFileSelectionActive = false;
            _logger.LogDebug("User {Username} ({UserId}) cancelled command selection", username, userId);
            await _outputService.ClearChatHistoryAsync(userId, session);
            await TrackMessageAsync(_outputService.RemoveReplyKeyboardAsync(userId, "Выбор команд отменен."), session);
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

    private async Task<Message?> TrackMessageAsync(Task<Message?> task, UserSession session)
    {
        var msg = await task;
        if (msg != null) session.AddBotMessageId(msg.Id);
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
