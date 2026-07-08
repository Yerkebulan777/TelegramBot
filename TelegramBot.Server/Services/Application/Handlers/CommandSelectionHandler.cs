using Microsoft.Extensions.Options;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class CommandSelectionHandler(
    KeyboardBuilder keyboardBuilder,
    TelegramOutputService outputService,
    MessageTrackingService messageTrackingService,
    IOptions<FileSystemOptions> options,
    ILogger<CommandSelectionHandler> logger) : CallbackHandlerBase(logger)
{
    private readonly FileSystemOptions _options = options.Value;

    protected override HashSet<string> SupportedPrefixes { get; } =
    [
        CallbackPrefixes.ApplyCommands,
        CallbackPrefixes.CancelCommandSelection
    ];

    public override Task HandleAsync(CallbackContext context, CancellationToken cancellationToken = default)
    {
        return context.ParsedCallback.Prefix switch
        {
            CallbackPrefixes.ApplyCommands => HandleApplyCommandsAsync(context, cancellationToken),
            CallbackPrefixes.CancelCommandSelection => HandleCancelCommandSelectionAsync(context, cancellationToken),
            _ => Task.CompletedTask
        };
    }

    private async Task HandleApplyCommandsAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var session = context.Session;

        if (session.PendingCommand.Count == 0)
        {
            return;
        }

        Logger.LogDebug("Command selection applied: user={Username} ({UserId}), count={Count}",
            context.Username, context.UserId, session.PendingCommand.Count);

        session.CurrentPath = _options.RootPath;
        session.IsFileSelectionActive = true;
        session.FileSelectionMessageId = context.MessageId;

        var keyboard = keyboardBuilder.GetSelectionKeyboard(context.UserId, session);
        await outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);

        await SendFileActionsReplyKeyboardAsync(context);

    }

    private async Task SendFileActionsReplyKeyboardAsync(CallbackContext context)
    {
        if (context.Session.LastActionsMessageId.HasValue)
        {
            await outputService.DeleteMessageAsync(context.UserId, context.Session.LastActionsMessageId.Value);
            context.Session.LastActionsMessageId = null;
        }

        var replyKeyboard = keyboardBuilder.GetFileActionsReplyKeyboard();
        var message = await messageTrackingService.TrackAsync(
            outputService.SendMessageWithReplyKeyboardAsync(context.UserId, "Действия:", replyKeyboard),
            context.Session);
        context.Session.LastActionsMessageId = message?.Id;
    }

    private async Task HandleCancelCommandSelectionAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        Logger.LogDebug("User {Username} ({UserId}) cancelled command selection", context.Username, context.UserId);

        // Сбрасываем состояние сессии
        context.Session.Reset(_options.RootPath);

        // Удаляем все отслеживаемые сообщения, ничего не выводим
        await outputService.ClearChatHistoryAsync(context.UserId, context.Session);

    }
}
