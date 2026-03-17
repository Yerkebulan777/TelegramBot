using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services.Application.Handlers;

/// <summary>
/// Handles automation command toggles (BIMDOC, CLASHREP, AUTORES).
/// </summary>
public sealed class AutomationCommandHandler : CommandToggleHandlerBase
{
    protected override IReadOnlyDictionary<string, (string Code, string DisplayName)> Commands { get; } =
        new Dictionary<string, (string Code, string DisplayName)>
        {
            [CallbackPrefixes.BimDoc] = ("BIMDOC", "BIM Doctor"),
            [CallbackPrefixes.ClashRep] = ("CLASHREP", "Clash Report"),
            [CallbackPrefixes.AutoRes] = ("AUTORES", "Auto Resolver")
        };

    protected override HashSet<string> SupportedPrefixes { get; } =
    [
        CallbackPrefixes.BimDoc,
        CallbackPrefixes.ClashRep,
        CallbackPrefixes.AutoRes
    ];

    public AutomationCommandHandler(
        IKeyboardBuilder keyboardBuilder,
        ITelegramOutputService outputService,
        ILogger<AutomationCommandHandler> logger) : base(keyboardBuilder, outputService, logger) { }

    protected override Task<InlineKeyboardMarkup> GetKeyboardAsync(IKeyboardBuilder keyboardBuilder, UserSession session)
        => keyboardBuilder.GetAutomationKeyboardAsync(session);
}
