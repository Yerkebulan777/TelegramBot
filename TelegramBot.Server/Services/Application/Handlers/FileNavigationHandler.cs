using Microsoft.Extensions.Options;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class FileNavigationHandler(
    KeyboardBuilder keyboardBuilder,
    TelegramOutputService outputService,
    IOptions<FileSystemOptions> options,
    ILogger<FileNavigationHandler> logger) : CallbackHandlerBase(logger)
{
    private readonly FileSystemOptions _options = options.Value;

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

        if (!ValidatePathWithinRoot(_options, newPath, "navigation", context))
        {
            await outputService.AnswerCallbackAsync(context.CallbackQueryId, "⚠ Недопустимый путь.");
            flow.ResetPathToRoot();
            return;
        }

        flow.NavigateTo(newPath);
        await outputService.AnswerCallbackAsync(context.CallbackQueryId, flow.CurrentPath);

        var keyboard = keyboardBuilder.GetSelectionKeyboard(context.UserId, session);
        await outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);
    }
}
