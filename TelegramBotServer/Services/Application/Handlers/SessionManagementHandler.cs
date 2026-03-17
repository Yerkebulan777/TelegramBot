using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotServer.DTOs;
using TelegramBotServer.Extensions;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services.Application.Handlers;

/// <summary>
/// Handles session management operations (view details, delete session, delete command).
/// </summary>
public sealed class SessionManagementHandler : CallbackHandlerBase
{
    private readonly IDataService _dataService;
    private readonly IKeyboardBuilder _keyboardBuilder;
    private readonly ITelegramOutputService _outputService;

    protected override HashSet<string> SupportedPrefixes { get; } =
    [
        CallbackPrefixes.SessionDetails,
        CallbackPrefixes.DeleteSession,
        CallbackPrefixes.DeleteCommand,
        CallbackPrefixes.BackToStatus
    ];

    public SessionManagementHandler(
        IDataService dataService,
        IKeyboardBuilder keyboardBuilder,
        ITelegramOutputService outputService,
        ILogger<SessionManagementHandler> logger) : base(logger)
    {
        _dataService = dataService;
        _keyboardBuilder = keyboardBuilder;
        _outputService = outputService;
    }

    public override async Task<bool> HandleAsync(CallbackContext context, CancellationToken cancellationToken = default)
    {
        return context.ParsedCallback.Prefix switch
        {
            CallbackPrefixes.SessionDetails => await HandleSessionDetailsAsync(context, cancellationToken),
            CallbackPrefixes.DeleteSession => await HandleDeleteSessionAsync(context, cancellationToken),
            CallbackPrefixes.DeleteCommand => await HandleDeleteCommandAsync(context, cancellationToken),
            CallbackPrefixes.BackToStatus => await HandleBackToStatusAsync(context, cancellationToken),
            _ => false
        };
    }

    private async Task<bool> HandleSessionDetailsAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var token = context.ParsedCallback.Argument;
        if (!int.TryParse(token, out int sessionId) || !sessionId.IsValidId())
        {
            LogInvalidInput("session ID", token, context.UserId);
            return true;
        }

        var session = context.Session;

        if (session.StatusLevel)
        {
            // Show session summary
            session.StatusLevel = false;
            session.SessionId = sessionId;

            var sessionStatus = await _dataService.GetSessionsStatusAsync(sessionId);

            var percentage = sessionStatus.TotalFiles > 0
                ? (100 * sessionStatus.DoneFiles) / sessionStatus.TotalFiles
                : 0;

            var reply = $"Статус: {sessionStatus.Status}\n" +
                        $"Файлов: {sessionStatus.TotalFiles}\n" +
                        $"Завершено: {sessionStatus.DoneFiles}\n" +
                        $"{percentage}%";

            var keyboard = await _keyboardBuilder.GetSessionStatusKeyboardAsync(sessionStatus, sessionId);
            await _outputService.EditMessageTextWithKeyboardAsync(context.UserId, context.MessageId, reply, keyboard);
        }
        else
        {
            // Show session commands detail
            session.StatusLevel = true;

            var sessionCommands = await _dataService.GetSessionsCommandsAsync(sessionId);
            var keyboard = await _keyboardBuilder.GetSessionCommandsKeyboardAsync(sessionCommands, sessionId);
            await _outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);
        }

        return true;
    }

    private async Task<bool> HandleDeleteSessionAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var token = context.ParsedCallback.Argument;
        if (!int.TryParse(token, out int sessionId) || !sessionId.IsValidId())
        {
            LogInvalidInput("session ID", token, context.UserId);
            return true;
        }

        if (!await _dataService.DeleteSessionAsync(sessionId))
            return true;

        context.Session.StatusLevel = true;

        var sessionsStatus = await _dataService.GetSessionsListAsync(context.UserId);
        var keyboard = await _keyboardBuilder.GetSessionsListKeyboardAsync(sessionsStatus);
        await _outputService.EditMessageTextWithKeyboardAsync(context.UserId, context.MessageId, "Сессии:", keyboard);

        return true;
    }

    private async Task<bool> HandleDeleteCommandAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var token = context.ParsedCallback.Argument;
        if (!int.TryParse(token, out int commandId) || !commandId.IsValidId())
        {
            LogInvalidInput("command ID", token, context.UserId);
            return true;
        }

        if (!await _dataService.DeleteCommandAsync(commandId))
            return true;

        // Remove button from keyboard
        if (context.Buttons != null)
        {
            foreach (var row in context.Buttons)
            {
                row.RemoveAll(btn => btn.CallbackData!.Contains($"{commandId}"));
            }
        }

        var newKeyboard = ConvertDtoToKeyboard(context.Buttons);

        // Check if session has remaining commands
        if (!await _dataService.CheckCommandsStatusAsync(context.Session.SessionId))
        {
            // All commands deleted - delete session and return to list
            if (await _dataService.DeleteSessionAsync(context.Session.SessionId))
            {
                context.Session.StatusLevel = true;
                var sessionsStatus = await _dataService.GetSessionsListAsync(context.UserId);
                var keyboard = await _keyboardBuilder.GetSessionsListKeyboardAsync(sessionsStatus);
                await _outputService.EditMessageTextWithKeyboardAsync(context.UserId, context.MessageId, "Сессии:", keyboard);
            }
        }
        else
        {
            // Update status display
            var sessionStatus = await _dataService.GetSessionsStatusAsync(context.Session.SessionId);

            var percentage = sessionStatus.TotalFiles > 0
                ? (100 * sessionStatus.DoneFiles) / sessionStatus.TotalFiles
                : 0;

            var reply = $"Статус: {sessionStatus.Status}\n" +
                        $"Файлов: {sessionStatus.TotalFiles}\n" +
                        $"Завершено: {sessionStatus.DoneFiles}\n" +
                        $"{percentage}%";

            await _outputService.EditMessageTextWithKeyboardAsync(context.UserId, context.MessageId, reply, newKeyboard);
        }

        return true;
    }

    private async Task<bool> HandleBackToStatusAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        context.Session.StatusLevel = true;

        var sessionsStatus = await _dataService.GetSessionsListAsync(context.UserId);
        var keyboard = await _keyboardBuilder.GetSessionsListKeyboardAsync(sessionsStatus);
        await _outputService.EditMessageTextWithKeyboardAsync(context.UserId, context.MessageId, "Сессии:", keyboard);

        return true;
    }

    private static InlineKeyboardMarkup ConvertDtoToKeyboard(List<List<ButtonDto>>? dto)
    {
        if (dto == null)
            return new InlineKeyboardMarkup(Array.Empty<InlineKeyboardButton[]>());

        var inlineKeyboard = dto
            .Select(row => row
                .Select(btn => InlineKeyboardButton.WithCallbackData(
                    btn.Text ?? "",
                    btn.CallbackData ?? ""))
                .ToArray())
            .ToArray();

        return new InlineKeyboardMarkup(inlineKeyboard);
    }
}
