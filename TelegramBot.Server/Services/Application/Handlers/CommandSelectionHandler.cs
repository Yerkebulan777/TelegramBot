using Microsoft.Extensions.Options;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class CommandSelectionHandler(
    KeyboardBuilder keyboardBuilder,
    TelegramOutputService outputService,
    FileActionsKeyboardService fileActionsKeyboardService,
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

        await fileActionsKeyboardService.RefreshAsync(context.UserId, context.Session);
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
