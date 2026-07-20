using Microsoft.Extensions.Options;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class FileSelectionHandler(
    KeyboardBuilder keyboardBuilder,
    TelegramOutputService outputService,
    MessageTrackingService messageTrackingService,
    TelegramBot.Server.Services.Infrastructure.FileSystem.FileSystemBrowser fileBrowser,
    IOptions<FileSystemOptions> options,
    ILogger<FileSelectionHandler> logger) : CallbackHandlerBase(logger)
{
    private readonly FileSystemOptions _options = options.Value;

    protected override HashSet<string> SupportedPrefixes { get; } =
    [
        CallbackPrefixes.File,
        CallbackPrefixes.SelectAllSectionFolders,
        CallbackPrefixes.OpenFolder
    ];

    public override Task HandleAsync(CallbackContext context, CancellationToken cancellationToken = default)
    {
        return context.ParsedCallback.Prefix switch
        {
            CallbackPrefixes.File => HandleFileToggleAsync(context, cancellationToken),
            CallbackPrefixes.SelectAllSectionFolders => HandleSelectAllAsync(context, cancellationToken),
            CallbackPrefixes.OpenFolder => HandleOpenFolderAsync(context, cancellationToken),
            _ => Task.CompletedTask
        };
    }

    private async Task HandleOpenFolderAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var session = context.Session;
        session.FileSelectionMessageId = context.MessageId;

        var newPath = string.IsNullOrEmpty(context.ParsedCallback.Argument)
            ? Path.GetDirectoryName(session.CurrentPath)
            : fileBrowser.ResolveSelectionPath(session.CurrentPath, context.ParsedCallback.Argument);

        if (!ValidatePathWithinRoot(_options, newPath, "folder navigation", context))
        {
            await outputService.AnswerCallbackAsync(context.CallbackQueryId, "⚠ Недопустимый путь.");
            return;
        }

        session.CurrentPath = newPath;

        await ReRenderSelectionAsync(context, "");
    }

    private async Task HandleSelectAllAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var session = context.Session;
        session.FileSelectionMessageId = context.MessageId;

        var path = string.IsNullOrEmpty(context.ParsedCallback.Argument)
            ? session.CurrentPath
            : context.ParsedCallback.Argument;

        if (ValidatePathWithinRoot(_options, path, "select-all", context))
        {
            var filePaths = fileBrowser.GetSelectableFiles(path);
            session.AddSelectedFiles(filePaths);
        }

        await ReRenderSelectionAsync(context, "Все файлы выбраны");

    }

    private async Task HandleFileToggleAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var session = context.Session;
        session.FileSelectionMessageId = context.MessageId;

        var filePath = fileBrowser.ResolveSelectionPath(session.CurrentPath, context.ParsedCallback.Argument);
        if (string.IsNullOrEmpty(filePath))
        {
            var replyKeyboard = keyboardBuilder.GetFileActionsReplyKeyboard();
            _ = await messageTrackingService.TrackAsync(
                outputService.SendMessageWithReplyKeyboardAsync(context.UserId, "⚠ Error: File not found.", replyKeyboard),
                context.Session);
            return;
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

        await ReRenderSelectionAsync(context, "");

    }

    /// <summary>
    /// Перестраивает selection-клавиатуру из текущего состояния сессии, обновляет
    /// сообщение и подтверждает callback. Общая механика toggle/select-all/open-folder.
    /// </summary>
    private async Task ReRenderSelectionAsync(CallbackContext context, string ackText)
    {
        var keyboard = keyboardBuilder.GetSelectionKeyboard(context.UserId, context.Session);
        await outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);
        await outputService.AnswerCallbackAsync(context.CallbackQueryId, ackText);
    }
}
