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
    private readonly IKeyboardBuilder _keyboardBuilder = keyboardBuilder;
    private readonly ITelegramOutputService _outputService = outputService;
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
            var errorMessage = await _outputService.SendErrorAsync(context.UserId, "Path not found.");
            if (errorMessage != null)
            {
                session.TrackMessage(errorMessage.Id);
            }

            return true;
        }

        if (!_options.IsPathWithinRoot(newPath))
        {
            Logger.LogWarning("Rejected navigation outside root. User={Username} ({UserId}), Path={Path}",
                context.Username, context.UserId, newPath);
            var errorMessage = await _outputService.SendErrorAsync(context.UserId, "Недопустимый путь.");
            if (errorMessage != null)
            {
                session.TrackMessage(errorMessage.Id);
            }

            session.CurrentPath = _options.RootPath;
            return true;
        }

        session.ClearSelectedFiles();
        session.CurrentPath = newPath;
        await _outputService.AnswerCallbackAsync(context.CallbackQueryId, session.CurrentPath);

        var keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(context.UserId, session);
        await _outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);

        await SendActionsReplyKeyboardAsync(context);

        return true;
    }

    private async Task SendActionsReplyKeyboardAsync(CallbackContext context)
    {
        var session = context.Session;

        if (session.LastActionsMessageId.HasValue)
        {
            await _outputService.DeleteMessageAsync(context.UserId, session.LastActionsMessageId.Value, session);
            session.LastActionsMessageId = null;
        }

        var replyKeyboard = _options.IsAtProjectLevel(session.CurrentPath)
            ? await _keyboardBuilder.GetProjectActionsReplyKeyboardAsync()
            : await _keyboardBuilder.GetSectionActionsReplyKeyboardAsync();

        var message = await _outputService.SendMessageWithReplyKeyboardAsync(context.UserId, "Действия:", replyKeyboard);
        if (message != null)
        {
            session.TrackMessage(message.Id);
            session.LastActionsMessageId = message.Id;
        }
    }
}
