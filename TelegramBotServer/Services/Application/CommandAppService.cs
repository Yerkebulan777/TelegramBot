using Microsoft.Extensions.Options;
using TelegramBotServer.Config;
using TelegramBotServer.DTOs;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;
using TelegramBotServer.Services.Application;
using TelegramBotServer.Services.Infrastructure.Telegram;

namespace TelegramBotServer.Services;

/// <summary>
/// Main application service for handling user commands and callbacks.
/// </summary>
public sealed class CommandAppService(
    IEnumerable<IUserCommandHandler> handlers,
    IDataService dataService,
    ITelegramOutputService outputService,
    ISessionManager sessionManager,
    IKeyboardBuilder keyboardBuilder,
    CallbackDispatcher callbackDispatcher,
    IOptions<FileSystemOptions> fileSystemOptions,
    ILogger<CommandAppService> logger) : ICommandAppService
{
    private readonly IEnumerable<IUserCommandHandler> _handlers = handlers;
    private readonly IDataService _dataService = dataService;
    private readonly ITelegramOutputService _outputService = outputService;
    private readonly ISessionManager _sessionManager = sessionManager;
    private readonly IKeyboardBuilder _keyboardBuilder = keyboardBuilder;
    private readonly CallbackDispatcher _callbackDispatcher = callbackDispatcher;
    private readonly ILogger<CommandAppService> _logger = logger;
    private readonly FileSystemOptions _options = fileSystemOptions.Value;

    public async Task HandleUserCommandAsync(MessageDto message, CancellationToken cancellationToken = default)
    {
        // Username и Text уже проверены в TelegramBotHostedService
        string text = message.Text!;
        long userId = message.UserId;
        string username = message.Username!;

        _logger.LogInformation("Received command '{Command}' from {Username} ({UserId})", text, username, userId);

        var session = _sessionManager.GetOrCreateSession(userId);

        if (await HandleReplyKeyboardActionAsync(userId, text, session, cancellationToken))
        {
            return;
        }

        var commandText = text.ToLower();
        var handler = _handlers.FirstOrDefault(h => h.Command == commandText);

        if (handler != null)
        {
            await handler.HandleAsync(message, session, cancellationToken);
        }
        else
        {
            _logger.LogDebug("No handler found for command '{Command}'", commandText);
        }
    }

    public async Task HandleCallbackAsync(CallbackQueryDto callback, CancellationToken cancellationToken = default)
    {
        if (callback.Username == null || callback.MessageText == null ||
            callback.CallbackData == null || callback.CallbackQueryId == null)
        {
            await _outputService.SendMessageAsync(callback.UserId, "Callback is empty.");
            return;
        }

        var session = _sessionManager.GetOrCreateSession(callback.UserId);

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

    private async Task<bool> HandleReplyKeyboardActionAsync(long userId, string messageText, UserSession session, CancellationToken cancellationToken)
    {
        if (messageText == ButtonTexts.ExportApply || messageText == ButtonTexts.AutomationApply)
        {
            if (session.PendingCommand.Count == 0)
            {
                await _outputService.SendMessageAsync(userId, "Сначала выберите хотя бы одну команду.");
                return true;
            }

            session.CurrentPath = _options.RootPath;
            var keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);
            await _outputService.SendMessageWithKeyboardAsync(userId, "Выберите файлы:", keyboard);
            await _outputService.RemoveReplyKeyboardAsync(userId, "Выбор подтвержден.");
            return true;
        }

        if (messageText == ButtonTexts.Cancel)
        {
            session.ClearPendingCommands();
            await _outputService.RemoveReplyKeyboardAsync(userId, "Выбор команд отменен.");
            return true;
        }

        return false;
    }
}
