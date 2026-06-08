using Microsoft.Extensions.Options;
using TelegramBot.Core.Config;
using TelegramBot.Core.Models;
using TelegramBot.Server.Constants;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class FileNavigationHandler(
    IKeyboardBuilder keyboardBuilder,
    ITelegramOutputService outputService,
    IOptions<FileSystemOptions> options,
    ILogger<FileNavigationHandler> logger) : CallbackHandlerBase(logger)
{
    private readonly FileSystemOptions _options = options.Value;

    protected override HashSet<string> SupportedPrefixes { get; } = [CallbackPrefixes.GoToParent];

    public override int Priority => HandlerPriorities.FileNavigation;

    protected override async Task<bool> HandleAsyncInternal(CallbackContext context, CancellationToken cancellationToken = default)
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

        await HandlerHelpers.SendActionsReplyKeyboardAsync(outputService, context,
            _options.IsAtProjectLevel(context.Session.CurrentPath)
                ? keyboardBuilder.GetProjectActionsReplyKeyboardAsync
                : keyboardBuilder.GetSectionActionsReplyKeyboardAsync);

        return true;
    }

    private async Task SendErrorWithKeyboardAsync(CallbackContext context, string message)
    {
        var session = context.Session;
        var replyKeyboard = _options.IsAtProjectLevel(session.CurrentPath)
            ? await keyboardBuilder.GetProjectActionsReplyKeyboardAsync()
            : await keyboardBuilder.GetSectionActionsReplyKeyboardAsync();
        var errorMessage = await outputService.SendMessageWithReplyKeyboardAsync(
            context.UserId, message, replyKeyboard);
        if (errorMessage != null)
        {
            session.TrackMessage(errorMessage.Id);
        }
    }
}
