using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services.Application.Handlers;

/// <summary>
/// Handles export command toggles (PDF, DWG, NWC, IFC).
/// </summary>
public sealed class ExportCommandHandler : CallbackHandlerBase
{
    private readonly IKeyboardBuilder _keyboardBuilder;
    private readonly ITelegramOutputService _outputService;

    private static readonly Dictionary<string, (string Code, string DisplayName)> Commands = new()
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
        ILogger<ExportCommandHandler> logger) : base(logger)
    {
        _keyboardBuilder = keyboardBuilder;
        _outputService = outputService;
    }

    public override async Task<bool> HandleAsync(CallbackContext context, CancellationToken cancellationToken = default)
    {
        var (code, displayName) = Commands[context.ParsedCallback.Prefix];

        ToggleCommand(context.Session, code, displayName);

        var keyboard = await _keyboardBuilder.GetCommandsKeyboardAsync(context.Session);
        await _outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);

        return true;
    }

    private static void ToggleCommand(UserSession session, string code, string displayName)
    {
        if (session.ContainsPendingCommand(code))
            session.RemovePendingCommand(code);
        else
            session.AddPendingCommand(code, displayName);
    }
}
