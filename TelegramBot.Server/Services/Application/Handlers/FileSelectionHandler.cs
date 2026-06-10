using Microsoft.Extensions.Options;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;
using TelegramBot.Server.Constants;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class FileSelectionHandler(
    IKeyboardBuilder keyboardBuilder,
    ITelegramOutputService outputService,
    IMessageTrackingDataService messageTrackingService,
    TelegramBot.Server.Services.Infrastructure.FileSystem.FileSystemBrowser fileBrowser,
    IOptions<FileSystemOptions> options,
    ILogger<FileSelectionHandler> logger) : CallbackHandlerBase(logger)
{
    private readonly FileSystemOptions _options = options.Value;

    protected override HashSet<string> SupportedPrefixes { get; } =
    [
        CallbackPrefixes.File,
        CallbackPrefixes.SelectAllSectionFolders
    ];

    public override int Priority => HandlerPriorities.FileSelection;

    protected override async Task<bool> HandleAsyncInternalAsync(CallbackContext context, CancellationToken cancellationToken = default)
    {
        return context.ParsedCallback.Prefix switch
        {
            CallbackPrefixes.File => await HandleFileToggleAsync(context, cancellationToken),
            CallbackPrefixes.SelectAllSectionFolders => await HandleSelectAllSectionFoldersAsync(context, cancellationToken),
            _ => false
        };
    }

    private async Task<bool> HandleSelectAllSectionFoldersAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var session = context.Session;
        session.FileSelectionMessageId = context.MessageId;

        var path = context.ParsedCallback.Argument;
        if (!string.IsNullOrEmpty(path))
        {
            var folderPaths = fileBrowser.GetSectionFolderPaths(path);
            session.AddSelectedFiles(folderPaths);
        }

        var keyboard = await keyboardBuilder.GetSelectionKeyboardAsync(context.UserId, session);
        await outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);
        await outputService.AnswerCallbackAsync(context.CallbackQueryId, "Все папки выбраны");

        return true;
    }

    private async Task<bool> HandleFileToggleAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var session = context.Session;
        session.FileSelectionMessageId = context.MessageId;

        var filePath = context.ParsedCallback.Argument;
        if (string.IsNullOrEmpty(filePath))
        {
            var replyKeyboard = _options.IsAtProjectLevel(session.CurrentPath)
                ? await keyboardBuilder.GetProjectActionsReplyKeyboardAsync()
                : await keyboardBuilder.GetSectionActionsReplyKeyboardAsync();
            var errorMessage = await outputService.SendMessageWithReplyKeyboardAsync(
                context.UserId, "⚠ Error: File not found.", replyKeyboard);
            if (errorMessage != null)
            {
                var sessionId = session.SessionId > 0 ? session.SessionId : (int?)null;
                await messageTrackingService.TrackMessageAsync(context.UserId, errorMessage.Id, sessionId);
            }

            return true;
        }

        if (_options.IsAtProjectLevel(session.CurrentPath))
        {
            // Одиночный выбор: сбросить предыдущий, выбрать новый
            session.ClearSelectedFiles();
            _=session.ToggleSelectedFile(filePath);
        }
        else
        {
            // Множественный выбор разделов
            _=session.ToggleSelectedFile(filePath);
        }

        var keyboard = await keyboardBuilder.GetSelectionKeyboardAsync(context.UserId, session);
        await outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);
        await outputService.AnswerCallbackAsync(context.CallbackQueryId, "");

        return true;
    }

}
