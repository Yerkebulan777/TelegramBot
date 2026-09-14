using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Server.Services.Application;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class RootPathHandler(
    SlashCommandService slashCommandService,
    TelegramOutputService outputService,
    ILogger<RootPathHandler> logger) : CallbackHandlerBase(logger)
{
    public override HashSet<string> SupportedPrefixes { get; } =
    [
        CallbackPrefixes.RootPath,
        CallbackPrefixes.ApplyPendingRootPath,
        CallbackPrefixes.CancelPendingRootPath
    ];

    public override async Task HandleAsync(CallbackContext context, CancellationToken cancellationToken = default)
    {
        if (context.ParsedCallback.Prefix is CallbackPrefixes.ApplyPendingRootPath or CallbackPrefixes.CancelPendingRootPath)
        {
            var message = await slashCommandService.DecidePendingRootPathChangeAsync(
                context.UserId,
                context.ParsedCallback.Argument,
                context.ParsedCallback.Prefix == CallbackPrefixes.ApplyPendingRootPath,
                cancellationToken);
            await outputService.AnswerCallbackAsync(context, message);
            return;
        }

        var canConfigure = await slashCommandService.BeginRootPathUpdateAsync(context.UserId, context.Session, cancellationToken);
        await outputService.AnswerCallbackAsync(
            context,
            canConfigure ? "Введите букву диска или UNC-путь." : "Корневой путь может менять только администратор.");
    }
}
