using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services.Application.Handlers;

/// <summary>
/// Обработчик переключения команд экспорта (PDF, DWG, NWC, IFC).
/// </summary>
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
        => keyboardBuilder.GetCommandsKeyboardAsync(session);
}
