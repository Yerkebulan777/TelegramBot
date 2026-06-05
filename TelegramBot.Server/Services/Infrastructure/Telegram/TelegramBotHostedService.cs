using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramBot.Core.DTOs;
using TelegramBot.Core.Interfaces;
using TelegramBot.Server.Config;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Infrastructure.Telegram;

public class TelegramBotHostedService(
    ITelegramBotClient botClient,
    ICommandAppService commandAppService,
    ILogger<TelegramBotHostedService> logger,
    ITelegramUpdateMapper inputService,
    ISessionManager sessionManager,
    IDataService dataService,
    ITelegramOutputService outputService) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Telegram polling starting");

        await CleanupStaleMessagesAsync(stoppingToken);
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
            var dto = await inputService.Map(update);
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

    private async Task CleanupStaleMessagesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var staleMessages = await dataService.GetAllTrackedMessagesAsync();
            var totalMessages = staleMessages.Sum(g => g.Count());
            if (totalMessages > 0)
            {
                logger.LogInformation("Startup cleanup: chats={ChatCount}, messages={MessageCount}",
                    staleMessages.Count, totalMessages);
            }

            foreach (var group in staleMessages)
            {
                var chatId = group.Key;
                foreach (var messageId in group)
                {
                    try
                    {
                        await outputService.DeleteMessageAsync(chatId, messageId);
                        await dataService.DeleteTrackedMessageAsync(chatId, messageId);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to delete stale message {MessageId} in chat {ChatId}", messageId, chatId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to cleanup stale messages on startup");
        }
    }

    private Task HandleErrorAsync(ITelegramBotClient client, Exception exception, CancellationToken token)
    {
        logger.LogError(exception, "Polling error");
        return Task.CompletedTask;
    }
}
