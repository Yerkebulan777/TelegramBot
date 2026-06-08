using Microsoft.Extensions.Options;
using TelegramBot.Core.Config;
using TelegramBot.Core.Models;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class CommandSelectionHandler(
    IKeyboardBuilder keyboardBuilder,
    ITelegramOutputService outputService,
    IOptions<FileSystemOptions> options,
    ILogger<CommandSelectionHandler> logger) : CallbackHandlerBase(logger)
{
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
        {
            return true;
        }

        Logger.LogDebug("User {Username} ({UserId}) applied command selection: [{Commands}]",
            context.Username, context.UserId, string.Join(", ", session.PendingCommand));

        session.CurrentPath = _options.RootPath;
        session.IsFileSelectionActive = true;
        session.FileSelectionMessageId = context.MessageId;

        var keyboard = await keyboardBuilder.GetSelectionKeyboardAsync(context.UserId, session);
        await outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);

        await SendActionsReplyKeyboardAsync(context);

        return true;
    }

    private Task SendActionsReplyKeyboardAsync(CallbackContext context)
        => HandlerHelpers.SendActionsReplyKeyboardAsync(outputService, context,
            keyboardBuilder.GetProjectActionsReplyKeyboardAsync);

    private async Task<bool> HandleCancelCommandSelectionAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        Logger.LogDebug("User {Username} ({UserId}) cancelled command selection", context.Username, context.UserId);

        // Сбрасываем состояние сессии
        context.Session.ClearPendingCommands();
        context.Session.IsFileSelectionActive = false;
        context.Session.LastActionsMessageId = null;

        // Удаляем все отслеживаемые сообщения, ничего не выводим
        await outputService.ClearChatHistoryAsync(context.UserId, context.Session);

        return true;
    }
}
