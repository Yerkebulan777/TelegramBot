using Microsoft.Extensions.Options;
using TelegramBot.Core.Config;
using TelegramBot.Core.Models;
using TelegramBot.Server.Constants;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class FileNavigationHandler(
    IFileSystemBrowser fileNavigationService,
    IKeyboardBuilder keyboardBuilder,
    ITelegramOutputService outputService,
    IOptions<FileSystemOptions> options,
    ILogger<FileNavigationHandler> logger) : CallbackHandlerBase(logger)
{
    private readonly IFileSystemBrowser _fileNavigationService = fileNavigationService;
    private readonly IKeyboardBuilder _keyboardBuilder = keyboardBuilder;
    private readonly ITelegramOutputService _outputService = outputService;
    private readonly FileSystemOptions _options = options.Value;

    protected override HashSet<string> SupportedPrefixes { get; } = [CallbackPrefixes.GoToParent];

    public override int Priority => HandlerPriorities.FileNavigation;

    protected override async Task<bool> HandleAsyncInternal(CallbackContext context, CancellationToken cancellationToken = default)
    {
        var session = context.Session;
        session.FileSelectionMessageId = context.MessageId;

        if (!_fileNavigationService.TryResolvePath(context.UserId, context.ParsedCallback.Argument, out var newPath) || newPath is null)
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
        var replyKeyboard = await _keyboardBuilder.GetProjectActionsReplyKeyboardAsync();
        var message = await _outputService.SendMessageWithReplyKeyboardAsync(context.UserId, "Действия:", replyKeyboard);
        if (message != null)
        {
            session.TrackMessage(message.Id);
        }

        return true;
    }
}
