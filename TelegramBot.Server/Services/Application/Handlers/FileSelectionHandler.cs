using Microsoft.Extensions.Options;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Data;
using TelegramBot.Server.Interfaces;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class FileSelectionHandler(
    KeyboardBuilder keyboardBuilder,
    ITelegramOutputService outputService,
    MessageTrackingDataService messageTrackingService,
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

    public override async Task<bool> HandleAsync(CallbackContext context, CancellationToken cancellationToken = default)
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

        var path = string.IsNullOrEmpty(context.ParsedCallback.Argument)
            ? session.CurrentPath
            : context.ParsedCallback.Argument;

        if (_options.IsPathWithinRoot(path))
        {
            var folderPaths = fileBrowser.GetSectionFolderPaths(path);
            session.AddSelectedFiles(folderPaths);
        }
        else
        {
            Logger.LogWarning(
                "Rejected select-all outside root. User={Username} ({UserId}), Path={Path}",
                context.Username,
                context.UserId,
                path);
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

        var filePath = fileBrowser.ResolveSelectionPath(session.CurrentPath, context.ParsedCallback.Argument);
        if (string.IsNullOrEmpty(filePath))
        {
            await HandlerHelpers.SendWarningWithReplyKeyboardAsync(
                outputService, messageTrackingService,
                context.UserId, context.Session,
                "⚠ Error: File not found.",
                keyboardBuilder.GetFileActionsReplyKeyboardAsync);
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
