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

    public async Task HandleUserCommandAsync(MessageDto message, CancellationToken cancellationToken = default)
    {
        // Username и Text уже проверены в TelegramBotHostedService
        string text = message.Text!;
        long userId = message.UserId;
        string username = message.Username!;

        _logger.LogInformation("Received command '{Command}' from {Username} ({UserId})", text, username, userId);

        var session = _sessionManager.GetOrCreateSession(userId);

        if (await HandleReplyKeyboardActionAsync(userId, username, text, session, cancellationToken))
        {
            return;
        }

        switch (text.ToLower())
        {
            case "/start":
                session.Reset(_options.RootPath);
                await SendStartMessageAsync(userId, username);
                break;

            case "/export":
                await StartCommandSelectionAsync(userId, session, isAutomation: false, cancellationToken);
                break;

            case "/status":
                session.Reset(_options.RootPath);
                session.IsInStatusView = true;

                var sessionsStatus = await _dataService.GetSessionsListAsync(userId);
                var keyboard = await _keyboardBuilder.GetSessionsListKeyboardAsync(sessionsStatus);
                await _outputService.SendMessageWithKeyboardAsync(userId, "Сессии:", keyboard);
                break;

            case "/automation":
                await StartCommandSelectionAsync(userId, session, isAutomation: true, cancellationToken);
                break;

            case "/help":
                session.Reset(_options.RootPath);
                await SendHelpMessageAsync(userId);
                break;
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

        await _outputService.SendMessageWithKeyboardAsync(userId, "Выберите команду:", commandKeyboard);

        var replyKeyboard = isAutomation
            ? await _keyboardBuilder.GetAutomationActionsReplyKeyboardAsync()
            : await _keyboardBuilder.GetExportActionsReplyKeyboardAsync();

        await _outputService.SendMessageWithReplyKeyboardAsync(userId, "Подтвердите выбор:", replyKeyboard);
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
                    await _outputService.SendMessageAsync(userId, "Сначала выберите хотя бы один файл.");
                    return true;
                }

                await DispatchFileSelectionCallbackAsync(userId, username, session, CallbackPrefixes.ApplyFiles, cancellationToken);
                session.IsFileSelectionActive = false;
                await _outputService.RemoveReplyKeyboardAsync(userId, "Выбор файлов подтвержден.");
                return true;
            }

            if (messageText == ButtonTexts.CancelSelection)
            {
                await DispatchFileSelectionCallbackAsync(userId, username, session, CallbackPrefixes.CancelSelection, cancellationToken);
                return true;
            }

            if (messageText == ButtonTexts.Cancel)
            {
                await DispatchFileSelectionCallbackAsync(userId, username, session, CallbackPrefixes.CancelFileSelection, cancellationToken);
                session.IsFileSelectionActive = false;

                await _outputService.RemoveReplyKeyboardAsync(userId, "Выбор файлов отменен.");

                var replyKeyboard = session.ContainsPendingCommand("BIMDOC")
                    || session.ContainsPendingCommand("CLASHREP")
                    || session.ContainsPendingCommand("AUTORES")
                    ? await _keyboardBuilder.GetAutomationActionsReplyKeyboardAsync()
                    : await _keyboardBuilder.GetExportActionsReplyKeyboardAsync();

                await _outputService.SendMessageWithReplyKeyboardAsync(userId, "Подтвердите выбор:", replyKeyboard);
                return true;
            }

            return false;
        }

        if (messageText == ButtonTexts.ExportApply || messageText == ButtonTexts.AutomationApply)
        {
            if (session.PendingCommand.Count == 0)
            {
                await _outputService.SendMessageAsync(userId, "Сначала выберите хотя бы одну команду.");
                return true;
            }

            session.CurrentPath = _options.RootPath;
            var keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);
            var selectionMessage = await _outputService.SendMessageWithKeyboardAsync(userId, "Выберите файлы:", keyboard);
            session.FileSelectionMessageId = selectionMessage?.Id;
            session.IsFileSelectionActive = true;

            var fileActionsReplyKeyboard = await _keyboardBuilder.GetFileActionsReplyKeyboardAsync(session);
            await _outputService.SendMessageWithReplyKeyboardAsync(userId, "Действия с файлами:", fileActionsReplyKeyboard);
            return true;
        }

        if (messageText == ButtonTexts.Cancel)
        {
            session.ClearPendingCommands();
            session.IsFileSelectionActive = false;
            await _outputService.RemoveReplyKeyboardAsync(userId, "Выбор команд отменен.");
            return true;
        }

        return false;
    }

    private async Task SendHelpMessageAsync(long userId)
    {
        var helpText = new StringBuilder()
            .AppendLine("/export - используется для экспорта в форматы PDF, DWG, NWC, IFC.")
            .AppendLine("/automation - используется для автоматизации задач. BIM Doctor, Clash Report, Auto Resolver")
            .AppendLine("/status - используется для проверки состояния выполнения команды отправленной пользователем.")
            .AppendLine("При отправке данной команды пользователю будет предоставлен список сессий с временем отправки на обработку.")
            .AppendLine("Пользователь может нажать на сессию для мониторинга процесса выполнения команды.")
            .AppendLine("Кроме того в предоставленном меню пользователь может полностью удалить сессию.")
            .ToString();

        await _outputService.SendMessageAsync(userId, helpText);
    }

    private async Task SendStartMessageAsync(long userId, string username)
    {
        var startText = new StringBuilder()
            .AppendLine($"Привет, {username}! 👋")
            .AppendLine("Я бот для работы с BIM-документами, автоматизации задач и экспорта файлов.\n")
            .AppendLine("Вот что я умею (нажмите на команду):")
            .AppendLine("🔹 /export - экспорт в форматы PDF, DWG, NWC")
            .AppendLine("🔹 /automation - задачи BIM автоматизации ")
            .AppendLine("🔹 /status - состояния выполнения задач")
            .AppendLine("🔹 /help - показать подробную справку")
            .ToString();

        await _outputService.SendMessageAsync(userId, startText);
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
}
