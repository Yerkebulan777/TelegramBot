using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class FileSelectionHandler(
    KeyboardBuilder keyboardBuilder,
    TelegramOutputService outputService,
    SlashCommandService slashCommandService,
    TelegramBot.Server.Services.Infrastructure.FileSystem.FileSystemBrowser fileBrowser,
    ILogger<FileSelectionHandler> logger) : CallbackHandlerBase(logger)
{
    public override HashSet<string> SupportedPrefixes { get; } =
    [
        CallbackPrefixes.File,
        CallbackPrefixes.SelectAllSectionFolders,
        CallbackPrefixes.OpenFolder,
        CallbackPrefixes.ConfirmFileSelection,
        CallbackPrefixes.CancelFileSelection
    ];

    public override Task HandleAsync(CallbackContext context, CancellationToken cancellationToken = default)
    {
        return context.ParsedCallback.Prefix switch
        {
            CallbackPrefixes.File => HandleFileToggleAsync(context, cancellationToken),
            CallbackPrefixes.SelectAllSectionFolders => HandleSelectAllAsync(context, cancellationToken),
            CallbackPrefixes.OpenFolder => HandleOpenFolderAsync(context, cancellationToken),
            CallbackPrefixes.ConfirmFileSelection => HandleConfirmAsync(context, cancellationToken),
            CallbackPrefixes.CancelFileSelection => HandleCancelAsync(context, cancellationToken),
            _ => Task.CompletedTask
        };
    }

    private async Task HandleConfirmAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        await outputService.AnswerCallbackAsync(context, "");
        await slashCommandService.ConfirmFileSelectionAsync(
            context.UserId, context.Username, context.Session, cancellationToken);
    }

    private async Task HandleCancelAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        await outputService.AnswerCallbackAsync(context, "");
        await slashCommandService.CancelSelectionAsync(context.UserId, context.Session, cancellationToken);
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
            if (!ValidatePathWithinRoot(session.RootPath, flow.CurrentPath, "folder navigation", context))
            {
                await outputService.AnswerCallbackAsync(context, "⚠ Недопустимый путь.");
                return;
            }
        }
        else
        {
            var newPath = fileBrowser.ResolveSelectionPath(session.RootPath, flow.CurrentPath, context.ParsedCallback.Argument);
            if (newPath == null || !ValidatePathWithinRoot(session.RootPath, newPath, "folder navigation", context))
            {
                await outputService.AnswerCallbackAsync(context, "⚠ Недопустимый путь.");
                return;
            }

            // С уровня проектов — одиночный выбор и углубление в 01_PROJECT: внутри перехода.
            flow.OpenFolder(newPath);
        }

        await ReRenderSelectionAsync(context, "");

    }

    private async Task HandleSelectAllAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var session = context.Session;
        var flow = session.Selection;
        session.FileSelectionMessageId = context.MessageId;

        var path = string.IsNullOrEmpty(context.ParsedCallback.Argument)
            ? flow.CurrentPath
            : context.ParsedCallback.Argument;

        if (ValidatePathWithinRoot(session.RootPath, path, "select-all", context))
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

        var filePath = fileBrowser.ResolveSelectionPath(session.RootPath, flow.CurrentPath, context.ParsedCallback.Argument);
        if (string.IsNullOrEmpty(filePath))
        {
            await outputService.AnswerCallbackAsync(context, "⚠ Файл не найден.");
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
        var keyboard = keyboardBuilder.GetSelectionKeyboard(context.Session);
        await outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);
        await outputService.AnswerCallbackAsync(context, ackText);
    }
}
