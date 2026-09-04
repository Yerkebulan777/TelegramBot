using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class FileNavigationHandler(
    KeyboardBuilder keyboardBuilder,
    TelegramOutputService outputService,
    ILogger<FileNavigationHandler> logger) : CallbackHandlerBase(logger)
{
    public override HashSet<string> SupportedPrefixes { get; } = [CallbackPrefixes.GoToParent];

    public override async Task HandleAsync(CallbackContext context, CancellationToken cancellationToken = default)
    {
        var session = context.Session;
        var flow = session.Selection;
        session.FileSelectionMessageId = context.MessageId;

        var newPath = context.ParsedCallback.Argument;
        if (string.IsNullOrEmpty(newPath))
        {
            await outputService.AnswerCallbackAsync(context.CallbackQueryId, "⚠ Путь не найден.");
            return;
        }

        if (!ValidatePathWithinRoot(session.RootPath, newPath, "navigation", context))
        {
            await outputService.AnswerCallbackAsync(context.CallbackQueryId, "⚠ Недопустимый путь.");
            flow.ResetPathToRoot();
            return;
        }

        flow.NavigateTo(newPath);
        await outputService.AnswerCallbackAsync(context.CallbackQueryId, flow.CurrentPath);

        var keyboard = keyboardBuilder.GetSelectionKeyboard(session);
        await outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);
    }
}
