using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class ExportCommandHandler(
    IKeyboardBuilder keyboardBuilder,
    ITelegramOutputService outputService,
    ILogger<ExportCommandHandler> logger) : CommandToggleHandlerBase(keyboardBuilder, outputService, logger)
{
    protected override IReadOnlyDictionary<string, (string Code, string DisplayName)> Commands { get; } =
        new Dictionary<string, (string Code, string DisplayName)>
        {
            [CallbackPrefixes.Pdf] = (CommandCodes.Pdf, "Export to PDF"),
            [CallbackPrefixes.Dwg] = (CommandCodes.Dwg, "Export to DWG"),
            [CallbackPrefixes.Nwc] = (CommandCodes.Nwc, "Export to NWC"),
            [CallbackPrefixes.Ifc] = (CommandCodes.Ifc, "Export to IFC"),
        };

    protected override Task<InlineKeyboardMarkup> GetKeyboardAsync(IKeyboardBuilder keyboardBuilder, UserSession session)
    {
        return keyboardBuilder.GetCommandsKeyboardAsync(session);
    }
}
