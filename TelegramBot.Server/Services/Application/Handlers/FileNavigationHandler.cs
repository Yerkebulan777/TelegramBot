using Microsoft.Extensions.Options;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class FileNavigationHandler(
    KeyboardBuilder keyboardBuilder,
    TelegramOutputService outputService,
    MessageTrackingService messageTrackingService,
    IOptions<FileSystemOptions> options,
    ILogger<FileNavigationHandler> logger) : CallbackHandlerBase(logger)
{
    private readonly FileSystemOptions _options = options.Value;

    protected override HashSet<string> SupportedPrefixes { get; } = [CallbackPrefixes.GoToParent];

    public override async Task HandleAsync(CallbackContext context, CancellationToken cancellationToken = default)
    {
        var session = context.Session;
        session.FileSelectionMessageId = context.MessageId;

        var newPath = context.ParsedCallback.Argument;
        if (string.IsNullOrEmpty(newPath))
        {
            await SendErrorWithKeyboardAsync(context, "⚠ Error: Path not found.");
            return;
        }

        if (!_options.IsPathWithinRoot(newPath))
        {
            Logger.LogWarning("Rejected navigation outside root. User={Username} ({UserId}), Path={Path}",
                context.Username, context.UserId, newPath);
            await SendErrorWithKeyboardAsync(context, "⚠ Error: Недопустимый путь.");
            session.CurrentPath = _options.RootPath;
            return;
        }

        session.ClearSelectedFiles();
        session.CurrentPath = newPath;
        await outputService.AnswerCallbackAsync(context.CallbackQueryId, session.CurrentPath);

        var keyboard = keyboardBuilder.GetSelectionKeyboard(context.UserId, session);
        await outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);

        await SendActionsReplyKeyboardAsync(context);

    }

    private async Task SendActionsReplyKeyboardAsync(CallbackContext context)
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

    private async Task SendErrorWithKeyboardAsync(CallbackContext context, string message)
    {
        var replyKeyboard = keyboardBuilder.GetFileActionsReplyKeyboard();
        _ = await messageTrackingService.TrackAsync(
            outputService.SendMessageWithReplyKeyboardAsync(context.UserId, message, replyKeyboard),
            context.Session);
    }
}
