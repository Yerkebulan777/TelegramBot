using Microsoft.Extensions.Options;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Data;
using TelegramBot.Server.Constants;
using TelegramBot.Server.Interfaces;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class FileNavigationHandler(
    KeyboardBuilder keyboardBuilder,
    ITelegramOutputService outputService,
    MessageTrackingDataService messageTrackingService,
    IOptions<FileSystemOptions> options,
    ILogger<FileNavigationHandler> logger) : CallbackHandlerBase(logger)
{
    private readonly FileSystemOptions _options = options.Value;

    protected override HashSet<string> SupportedPrefixes { get; } = [CallbackPrefixes.GoToParent];

    public override int Priority => HandlerPriorities.FileNavigation;

    protected override async Task<bool> HandleAsyncInternalAsync(CallbackContext context, CancellationToken cancellationToken = default)
    {
        var session = context.Session;
        session.FileSelectionMessageId = context.MessageId;

        var newPath = context.ParsedCallback.Argument;
        if (string.IsNullOrEmpty(newPath))
        {
            await SendErrorWithKeyboardAsync(context, "⚠ Error: Path not found.");
            return true;
        }

        if (!_options.IsPathWithinRoot(newPath))
        {
            Logger.LogWarning("Rejected navigation outside root. User={Username} ({UserId}), Path={Path}",
                context.Username, context.UserId, newPath);
            await SendErrorWithKeyboardAsync(context, "⚠ Error: Недопустимый путь.");
            session.CurrentPath = _options.RootPath;
            return true;
        }

        session.ClearSelectedFiles();
        session.CurrentPath = newPath;
        await outputService.AnswerCallbackAsync(context.CallbackQueryId, session.CurrentPath);

        var keyboard = await keyboardBuilder.GetSelectionKeyboardAsync(context.UserId, session);
        await outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);

        await HandlerHelpers.SendActionsReplyKeyboardAsync(outputService, messageTrackingService, context,
            keyboardBuilder.GetFileActionsReplyKeyboardAsync);

        return true;
    }

    private Task SendErrorWithKeyboardAsync(CallbackContext context, string message)
    {
        return HandlerHelpers.SendWarningWithReplyKeyboardAsync(
            outputService, messageTrackingService,
            context.UserId, context.Session, message,
            keyboardBuilder.GetFileActionsReplyKeyboardAsync);
    }
}
