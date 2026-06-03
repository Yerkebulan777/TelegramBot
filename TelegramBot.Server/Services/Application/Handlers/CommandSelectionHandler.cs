using Microsoft.Extensions.Options;
using TelegramBot.Core.Config;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class CommandSelectionHandler(
    IKeyboardBuilder keyboardBuilder,
    ITelegramOutputService outputService,
    IOptions<FileSystemOptions> options,
    ILogger<CommandSelectionHandler> logger) : CallbackHandlerBase(logger)
{
    private readonly IKeyboardBuilder _keyboardBuilder = keyboardBuilder;
    private readonly ITelegramOutputService _outputService = outputService;
    private readonly FileSystemOptions _options = options.Value;

    protected override HashSet<string> SupportedPrefixes { get; } =
    [
        CallbackPrefixes.ApplyCommands,
        CallbackPrefixes.CancelCommandSelection
    ];

    protected override async Task<bool> HandleAsyncInternal(CallbackContext context, CancellationToken cancellationToken = default)
    {
        return context.ParsedCallback.Prefix switch
        {
            CallbackPrefixes.ApplyCommands => await HandleApplyCommandsAsync(context, cancellationToken),
            CallbackPrefixes.CancelCommandSelection => await HandleCancelCommandSelectionAsync(context, cancellationToken),
            _ => false
        };
    }

    private async Task<bool> HandleApplyCommandsAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var session = context.Session;

        if (session.PendingCommand.Count == 0)
            return true;

        Logger.LogInformation("User {Username} ({UserId}) applied command selection: [{Commands}]",
            context.Username, context.UserId, string.Join(", ", session.PendingCommand));

        session.CurrentPath = _options.RootPath;
        session.IsFileSelectionActive = true;
        session.FileSelectionMessageId = context.MessageId;

        var keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(context.UserId, session);
        await _outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);
        var replyKeyboard = await _keyboardBuilder.GetProjectActionsReplyKeyboardAsync();
        var message = await _outputService.SendMessageWithReplyKeyboardAsync(context.UserId, "Действия:", replyKeyboard);
        if (message != null)
            session.TrackMessage(message.Id);

        return true;
    }

    private async Task<bool> HandleCancelCommandSelectionAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        Logger.LogInformation("User {Username} ({UserId}) cancelled command selection", context.Username, context.UserId);
        await _outputService.ClearChatHistoryAsync(context.UserId, context.Session);

        context.Session.ClearPendingCommands();
        context.Session.IsFileSelectionActive = false;

        var cancelMsg = await _outputService.RemoveReplyKeyboardAsync(context.UserId, "Выбор команд отменен.");
        if (cancelMsg != null)
            context.Session.TrackMessage(cancelMsg.Id);

        return true;
    }
}
