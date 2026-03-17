using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services.Application.Handlers;

/// <summary>
/// Handles automation command toggles (BIMDOC, CLASHREP, AUTORES).
/// </summary>
public sealed class AutomationCommandHandler : CallbackHandlerBase
{
    private readonly IKeyboardBuilder _keyboardBuilder;
    private readonly ITelegramOutputService _outputService;

    private static readonly Dictionary<string, (string Code, string DisplayName)> Commands = new()
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
        ILogger<AutomationCommandHandler> logger) : base(logger)
    {
        _keyboardBuilder = keyboardBuilder;
        _outputService = outputService;
    }

    public override async Task<bool> HandleAsync(CallbackContext context, CancellationToken cancellationToken = default)
    {
        var (code, displayName) = Commands[context.ParsedCallback.Prefix];

        ToggleCommand(context.Session, code, displayName);

        var keyboard = await _keyboardBuilder.GetAutomationKeyboardAsync(context.Session);
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
