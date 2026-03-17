using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services.Application.Handlers;

/// <summary>
/// Handles export command toggles (PDF, DWG, NWC, IFC).
/// </summary>
public sealed class ExportCommandHandler : CommandToggleHandlerBase
{
    protected override IReadOnlyDictionary<string, (string Code, string DisplayName)> Commands { get; } =
        new Dictionary<string, (string Code, string DisplayName)>
        {
            [CallbackPrefixes.Pdf] = ("PDF", "Export to PDF"),
            [CallbackPrefixes.Dwg] = ("DWG", "Export to DWG"),
            [CallbackPrefixes.Nwc] = ("NWC", "Export to NWC"),
            [CallbackPrefixes.Ifc] = ("IFC", "Export to IFC")
        };

    protected override HashSet<string> SupportedPrefixes { get; } =
    [
        CallbackPrefixes.Pdf,
        CallbackPrefixes.Dwg,
        CallbackPrefixes.Nwc,
        CallbackPrefixes.Ifc
    ];

    public ExportCommandHandler(
        IKeyboardBuilder keyboardBuilder,
        ITelegramOutputService outputService,
        ILogger<ExportCommandHandler> logger) : base(keyboardBuilder, outputService, logger) { }

    protected override Task<InlineKeyboardMarkup> GetKeyboardAsync(IKeyboardBuilder keyboardBuilder, UserSession session)
        => keyboardBuilder.GetCommandsKeyboardAsync(session);
}
