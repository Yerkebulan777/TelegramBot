using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class CommandSelectionHandler(
    KeyboardBuilder keyboardBuilder,
    TelegramOutputService outputService,
    ILogger<CommandSelectionHandler> logger) : CallbackHandlerBase(logger)
{
    public override HashSet<string> SupportedPrefixes { get; } =
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

        if (!session.Selection.ApplyCommands())
        {
            return;
        }

        Logger.LogDebug("Command selection applied: user={Username} ({UserId}), count={Count}",
            context.Username, context.UserId, session.Selection.PendingCommands.Count);

        session.FileSelectionMessageId = context.MessageId;

        var keyboard = keyboardBuilder.GetSelectionKeyboard(session);
        await outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);
    }

    private async Task HandleCancelCommandSelectionAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        Logger.LogDebug("User {Username} ({UserId}) cancelled command selection", context.Username, context.UserId);

        // Сбрасываем состояние сессии
        context.Session.Reset(context.Session.RootPath);

        // Удаляем все отслеживаемые сообщения, ничего не выводим
        await outputService.ClearChatHistoryAsync(context.UserId, context.Session, cancellationToken);
    }
}
