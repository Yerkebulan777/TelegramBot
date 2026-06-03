using Microsoft.Extensions.Options;
using TelegramBot.Core.Config;
using TelegramBot.Core.Models;
using TelegramBot.Server.Constants;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class FileSelectionHandler(
    IKeyboardBuilder keyboardBuilder,
    ITelegramOutputService outputService,
    IOptions<FileSystemOptions> options,
    ILogger<FileSelectionHandler> logger) : CallbackHandlerBase(logger)
{
    private readonly IKeyboardBuilder _keyboardBuilder = keyboardBuilder;
    private readonly ITelegramOutputService _outputService = outputService;
    private readonly FileSystemOptions _options = options.Value;

    protected override HashSet<string> SupportedPrefixes { get; } =
    [
        CallbackPrefixes.File
    ];

    public override int Priority => HandlerPriorities.FileSelection;

    protected override async Task<bool> HandleAsyncInternal(CallbackContext context, CancellationToken cancellationToken = default)
    {
        return context.ParsedCallback.Prefix switch
        {
            CallbackPrefixes.File => await HandleFileToggleAsync(context, cancellationToken),
            _ => false
        };
    }

    private async Task<bool> HandleFileToggleAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var session = context.Session;
        session.FileSelectionMessageId = context.MessageId;

        if (!session.PathMap.TryGetValue(context.ParsedCallback.Argument, out var filePath) || filePath == null)
        {
            var errorMessage = await _outputService.SendErrorAsync(context.UserId, "File not found.");
            if (errorMessage != null)
                session.TrackMessage(errorMessage.Id);
            return true;
        }

        if (_options.IsAtProjectLevel(session.CurrentPath))
        {
            // Одиночный выбор: сбросить предыдущий, выбрать новый
            session.ClearSelectedFiles();
            session.ToggleSelectedFile(filePath);
            Logger.LogInformation("User {Username} ({UserId}) selected project '{Project}'",
                context.Username, context.UserId, Path.GetFileName(filePath));
        }
        else
        {
            // Множественный выбор разделов
            bool wasSelected = session.SelectedFiles.Contains(filePath);
            session.ToggleSelectedFile(filePath);
            Logger.LogInformation("User {Username} ({UserId}) {Action} section '{Section}' (total: {Count})",
                context.Username, context.UserId, wasSelected ? "deselected" : "selected",
                Path.GetFileName(filePath), session.SelectedFiles.Count);
        }

        var keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(context.UserId, session);
        await _outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);
        await _outputService.AnswerCallbackAsync(context.CallbackQueryId, "");

        return true;
    }

}
