using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services.Application.Handlers;

/// <summary>
/// Обработчик переключения команд автоматизации (BIMDOC, CLASHREP, AUTORES).
/// </summary>
public sealed class AutomationCommandHandler(
    IKeyboardBuilder keyboardBuilder,
    ITelegramOutputService outputService,
    ILogger<AutomationCommandHandler> logger) : CommandToggleHandlerBase(keyboardBuilder, outputService, logger)
{
    protected override IReadOnlyDictionary<string, (string Code, string DisplayName)> Commands { get; } =

        new Dictionary<string, (string Code, string DisplayName)>
        {
            [CallbackPrefixes.BimDoc] = ("BIMDOC", "BIM Doctor"),
            [CallbackPrefixes.AutoRes] = ("AUTORES", "Auto Resolver"),
            [CallbackPrefixes.ClashRep] = ("CLASHREP", "Clash Report"),
        };

    protected override HashSet<string> SupportedPrefixes { get; } =
    [
        CallbackPrefixes.BimDoc,
        CallbackPrefixes.ClashRep,
        CallbackPrefixes.AutoRes
    ];

    /// <summary>
    ///  Retrieves an inline keyboard markup for the specified user session using the provided keyboard builder.
    /// </summary>
    protected override Task<InlineKeyboardMarkup> GetKeyboardAsync(IKeyboardBuilder keyboardBuilder, UserSession session)
    {
        return keyboardBuilder.GetAutomationKeyboardAsync(session);
    }
}
