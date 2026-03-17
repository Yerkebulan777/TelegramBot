using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services.Application.Handlers;

/// <summary>
/// Base class for handlers that toggle named commands on/off in the user session.
/// </summary>
public abstract class CommandToggleHandlerBase : CallbackHandlerBase
{
    private readonly IKeyboardBuilder _keyboardBuilder;
    private readonly ITelegramOutputService _outputService;

    protected CommandToggleHandlerBase(
        IKeyboardBuilder keyboardBuilder,
        ITelegramOutputService outputService,
        ILogger logger) : base(logger)
    {
        _keyboardBuilder = keyboardBuilder;
        _outputService = outputService;
    }

    /// <summary>
    /// Maps callback prefix → (code, displayName) for each toggleable command.
    /// </summary>
    protected abstract IReadOnlyDictionary<string, (string Code, string DisplayName)> Commands { get; }

    /// <summary>
    /// Returns the keyboard to display after toggling.
    /// </summary>
    protected abstract Task<InlineKeyboardMarkup> GetKeyboardAsync(IKeyboardBuilder keyboardBuilder, UserSession session);

    public override async Task<bool> HandleAsync(CallbackContext context, CancellationToken cancellationToken = default)
    {
        var (code, displayName) = Commands[context.ParsedCallback.Prefix];

        if (context.Session.ContainsPendingCommand(code))
            context.Session.RemovePendingCommand(code);
        else
            context.Session.AddPendingCommand(code, displayName);

        var keyboard = await GetKeyboardAsync(_keyboardBuilder, context.Session);
        await _outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);

        return true;
    }
}
