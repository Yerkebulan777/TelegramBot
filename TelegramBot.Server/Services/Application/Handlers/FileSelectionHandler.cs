using Microsoft.Extensions.Options;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class FileSelectionHandler(
    KeyboardBuilder keyboardBuilder,
    TelegramOutputService outputService,
    FileActionsKeyboardService fileActionsKeyboardService,
    TelegramBot.Server.Services.Infrastructure.FileSystem.FileSystemBrowser fileBrowser,
    IOptions<FileSystemOptions> options,
    ILogger<FileSelectionHandler> logger) : CallbackHandlerBase(logger)
{
    private readonly FileSystemOptions _options = options.Value;

    public override HashSet<string> SupportedPrefixes { get; } =
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
        var flow = session.Selection;
        session.FileSelectionMessageId = context.MessageId;

        if (string.IsNullOrEmpty(context.ParsedCallback.Argument))
        {
            // Шаг назад: из 01_PROJECT — на корень, из раздела — в 01_PROJECT.
            flow.GoBack();
            if (!ValidatePathWithinRoot(_options, flow.CurrentPath, "folder navigation", context))
            {
                await outputService.AnswerCallbackAsync(context.CallbackQueryId, "⚠ Недопустимый путь.");
                return;
            }
        }
        else
        {
            var newPath = fileBrowser.ResolveSelectionPath(flow.CurrentPath, context.ParsedCallback.Argument);
            if (newPath == null || !ValidatePathWithinRoot(_options, newPath, "folder navigation", context))
            {
                await outputService.AnswerCallbackAsync(context.CallbackQueryId, "⚠ Недопустимый путь.");
                return;
            }

            // С уровня проектов — одиночный выбор и углубление в 01_PROJECT: внутри перехода.
            flow.OpenFolder(newPath);
        }

        await ReRenderSelectionAsync(context, "");

        if (flow.CurrentLevel == SelectionFlow.Level.Files)
        {
            await fileActionsKeyboardService.RefreshAsync(context.UserId, session);
        }
        else
        {
            await fileActionsKeyboardService.HideAsync(context.UserId, session);
        }
    }

    private async Task HandleSelectAllAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var session = context.Session;
        var flow = session.Selection;
        session.FileSelectionMessageId = context.MessageId;

        var path = string.IsNullOrEmpty(context.ParsedCallback.Argument)
            ? flow.CurrentPath
            : context.ParsedCallback.Argument;

        if (ValidatePathWithinRoot(_options, path, "select-all", context))
        {
            flow.AddFiles(fileBrowser.GetSelectableFiles(path));
        }

        await ReRenderSelectionAsync(context, "Все файлы выбраны");
    }

    private async Task HandleFileToggleAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var session = context.Session;
        var flow = session.Selection;
        session.FileSelectionMessageId = context.MessageId;

        var filePath = fileBrowser.ResolveSelectionPath(flow.CurrentPath, context.ParsedCallback.Argument);
        if (string.IsNullOrEmpty(filePath))
        {
            await fileActionsKeyboardService.SendErrorAsync(context.UserId, "⚠ Error: File not found.", context.Session);
            return;
        }

        // Одиночный выбор на уровне проектов и тоггл на уровнях разделов/файлов — внутри перехода.
        _ = flow.ToggleFile(filePath);

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
