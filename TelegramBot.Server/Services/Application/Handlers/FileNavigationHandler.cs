using Microsoft.Extensions.Options;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class FileNavigationHandler(
    KeyboardBuilder keyboardBuilder,
    TelegramOutputService outputService,
    FileActionsKeyboardService fileActionsKeyboardService,
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
            await fileActionsKeyboardService.SendErrorAsync(context.UserId, "⚠ Error: Path not found.", context.Session);
            return;
        }

        if (!ValidatePathWithinRoot(_options, newPath, "navigation", context))
        {
            await fileActionsKeyboardService.SendErrorAsync(context.UserId, "⚠ Error: Недопустимый путь.", context.Session);
            session.CurrentPath = _options.RootPath;
            return;
        }

        session.ClearSelectedFiles();
        session.CurrentPath = newPath;
        await outputService.AnswerCallbackAsync(context.CallbackQueryId, session.CurrentPath);

        var keyboard = keyboardBuilder.GetSelectionKeyboard(context.UserId, session);
        await outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);

        await fileActionsKeyboardService.RefreshAsync(context.UserId, context.Session);
    }
}
