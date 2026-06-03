using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class AutomationCommandHandler(
    IKeyboardBuilder keyboardBuilder,
    ITelegramOutputService outputService,
    ILogger<AutomationCommandHandler> logger) : CommandToggleHandlerBase(keyboardBuilder, outputService, logger)
{
    protected override IReadOnlyDictionary<string, (string Code, string DisplayName)> Commands { get; } =
        new Dictionary<string, (string Code, string DisplayName)>
        {
            [CallbackPrefixes.BimDoc] = (CommandCodes.BimDoc, "BIM Doctor"),
            [CallbackPrefixes.AutoRes] = (CommandCodes.AutoRes, "Auto Resolver"),
            [CallbackPrefixes.ClashRep] = (CommandCodes.ClashRep, "Clash Report"),
        };

    protected override Task<InlineKeyboardMarkup> GetKeyboardAsync(IKeyboardBuilder keyboardBuilder, UserSession session)
    {
        return keyboardBuilder.GetAutomationKeyboardAsync(session);
    }
}
