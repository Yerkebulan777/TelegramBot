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
    public override HashSet<string> SupportedPrefixes { get; } = [CallbackPrefixes.RootPath];

    public override async Task HandleAsync(CallbackContext context, CancellationToken cancellationToken = default)
    {
        await outputService.AnswerCallbackAsync(context.CallbackQueryId, "Введите UNC-путь.");
        await slashCommandService.BeginRootPathUpdateAsync(context.UserId, context.Session);
    }
}
