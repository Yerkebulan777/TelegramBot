using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramBot.Core.DTOs;
using TelegramBot.Core.Interfaces;
using TelegramBot.Server.Config;

namespace TelegramBot.Server.Services.Infrastructure.Telegram;

public class TelegramBotHostedService(
    ITelegramBotClient botClient,
    ICommandAppService commandAppService,
    ILogger<TelegramBotHostedService> logger,
    TelegramUpdateMapper inputService,
    ISessionManager sessionManager) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Telegram polling starting");

        await BotCommandsSetup.ConfigureAsync(botClient, logger);

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery]
        };

        var updateHandler = new DefaultUpdateHandler(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandleErrorAsync
        );

        botClient.StartReceiving(updateHandler, receiverOptions, stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            // Expected when the host is stopping
        }

        logger.LogInformation("Telegram polling stopped");
    }

    public async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken token)
    {
        try
        {
            var dto = await inputService.MapAsync(update);
            logger.LogDebug("Update received: id={UpdateId}, type={UpdateType}, dto={DtoType}",
                update.Id, update.Type, dto?.GetType().Name ?? "null");

            switch (dto)
            {
                case MessageDto message:
                    using (await sessionManager.AcquireUserLockAsync(message.UserId))
                    {
                        _ = sessionManager.GetOrCreateSession(message.UserId);

                        if (message.Text == null)
                        {
                            logger.LogWarning("Received message with null Text from {Username} ({UserId})", message.Username, message.UserId);
                            return;
                        }

                        await commandAppService.HandleUserCommandAsync(message, token);
                    }
                    break;
                case CallbackQueryDto callback:
                    if (callback.CallbackQueryId == null)
                    {
                        logger.LogWarning("Received callback with null CallbackQueryId from {Username} ({UserId})", callback.Username, callback.UserId);
                        return;
                    }
                    using (await sessionManager.AcquireUserLockAsync(callback.UserId))
                    {
                        _ = sessionManager.GetOrCreateSession(callback.UserId);
                        await commandAppService.HandleCallbackAsync(callback, token);
                        await bot.AnswerCallbackQuery(callback.CallbackQueryId, cancellationToken: token);
                    }
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Update handling was cancelled");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception processing update {UpdateId}", update.Id);
        }
    }

    private Task HandleErrorAsync(ITelegramBotClient client, Exception exception, CancellationToken token)
    {
        logger.LogError(exception, "Polling error");
        return Task.CompletedTask;
    }
}
