using TelegramBot.Core.Models;
using TelegramBot.Server.Models;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class CommandToggleHandler(
    KeyboardBuilder keyboardBuilder,
    TelegramOutputService outputService,
    ILogger<CommandToggleHandler> logger) : CallbackHandlerBase(logger)
{
    public override IEnumerable<string> GetSupportedPrefixes()
    {
        return CommandCatalog.All.Select(c => c.Prefix);
    }

    public override async Task HandleAsync(CallbackContext context, CancellationToken cancellationToken = default)
    {
        if (!CommandCatalog.TryGetByPrefix(context.ParsedCallback.Prefix, out var command))
        {
            return;
        }

        if (context.Session.ContainsPendingCommand(command.Code))
        {
            _ = context.Session.RemovePendingCommand(command.Code);
        }
        else
        {
            context.Session.AddPendingCommand(command.Code, command.Name);
        }

        var keyboard = keyboardBuilder.GetCommandKeyboard(command.Group, context.Session);
        await outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);

    }
}
