using TelegramBotServer.Config;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services.Application.Handlers;

/// <summary>
/// Обработчик операций выбора команд (применить, отмена) и смены режима выбора.
/// </summary>
public sealed class CommandSelectionHandler : CallbackHandlerBase
{
    private readonly IKeyboardBuilder _keyboardBuilder;
    private readonly ITelegramOutputService _outputService;
    private readonly FileSystemOptions _options;

    protected override HashSet<string> SupportedPrefixes { get; } =
    [
        CallbackPrefixes.SelectionMode,
        CallbackPrefixes.ApplyCommands,
        CallbackPrefixes.CancelCommandSelection
    ];

    public CommandSelectionHandler(
        IKeyboardBuilder keyboardBuilder,
        ITelegramOutputService outputService,
        Microsoft.Extensions.Options.IOptions<FileSystemOptions> options,
        ILogger<CommandSelectionHandler> logger) : base(logger)
    {
        _keyboardBuilder = keyboardBuilder;
        _outputService = outputService;
        _options = options.Value;
    }

    protected override async Task<bool> HandleAsyncInternal(CallbackContext context, CancellationToken cancellationToken = default)
    {
        return context.ParsedCallback.Prefix switch
        {
            CallbackPrefixes.SelectionMode => await HandleSelectionModeAsync(context, cancellationToken),
            CallbackPrefixes.ApplyCommands => await HandleApplyCommandsAsync(context, cancellationToken),
            CallbackPrefixes.CancelCommandSelection => await HandleCancelCommandSelectionAsync(context, cancellationToken),
            _ => false
        };
    }

    private async Task<bool> HandleSelectionModeAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var session = context.Session;

        // Cycle through selection modes
        session.SelectionType = session.SelectionType switch
        {
            SelectionMode.Files => SelectionMode.Sections,
            SelectionMode.Sections => SelectionMode.Projects,
            SelectionMode.Projects => SelectionMode.Files,
            _ => SelectionMode.Files
        };

        session.ResetNavigation(_options.RootPath);

        var keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(context.UserId, session);
        await _outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);

        return true;
    }

    private async Task<bool> HandleApplyCommandsAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var session = context.Session;

        if (session.PendingCommand.Count == 0)
            return true;

        session.CurrentPath = _options.RootPath;

        var keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(context.UserId, session);
        await _outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);
        await _outputService.RemoveReplyKeyboardAsync(context.UserId, "Выбор подтвержден.");

        return true;
    }

    private async Task<bool> HandleCancelCommandSelectionAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        context.Session.ClearPendingCommands();

        await _outputService.RemoveReplyKeyboardAsync(context.UserId, "Выбор команд отменен.");
        await _outputService.DeleteMessageAsync(context.ChatId, context.MessageId);

        return true;
    }
}
