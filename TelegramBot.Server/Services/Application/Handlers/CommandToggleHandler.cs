using TelegramBot.Core.Models;
using TelegramBot.Server.Interfaces;
using TelegramBot.Server.Models;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class CommandToggleHandler(
    IKeyboardBuilder keyboardBuilder,
    ITelegramOutputService outputService,
    ILogger<CommandToggleHandler> logger) : CallbackHandlerBase(logger)
{
    private readonly IKeyboardBuilder _keyboardBuilder = keyboardBuilder;
    private readonly ITelegramOutputService _outputService = outputService;

    public override bool CanHandle(string prefix)
    {
        return CommandCatalog.TryGetByPrefix(prefix, out _);
    }

    protected override async Task<bool> HandleAsyncInternal(CallbackContext context, CancellationToken cancellationToken = default)
    {
        if (!CommandCatalog.TryGetByPrefix(context.ParsedCallback.Prefix, out var command))
        {
            return false;
        }

        if (context.Session.ContainsPendingCommand(command.Code))
        {
            _ = context.Session.RemovePendingCommand(command.Code);
        }
        else
        {
            context.Session.AddPendingCommand(command.Code, command.Name);
        }

        var keyboard = await _keyboardBuilder.GetCommandKeyboardAsync(command.Group, context.Session);
        await _outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);

        return true;
    }
}
