using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramBot.Core.DTOs;
using TelegramBot.Core.Interfaces;
using TelegramBot.Server.Config;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Infrastructure.Telegram;

public class TelegramBotHostedService : BackgroundService
{
    private readonly ITelegramBotClient _botClient;
    private readonly ICommandAppService _commandAppService;
    private readonly ILogger<TelegramBotHostedService> _logger;
    private readonly ITelegramUpdateMapper _inputService;
    private readonly ISessionManager _sessionManager;
    private readonly IDataService _dataService;
    private readonly ITelegramOutputService _outputService;

    public TelegramBotHostedService(
        ITelegramBotClient botClient,
        ICommandAppService commandAppService,
        ILogger<TelegramBotHostedService> logger,
        ITelegramUpdateMapper inputService,
        ISessionManager sessionManager,
        IDataService dataService,
        ITelegramOutputService outputService)
    {
        _botClient = botClient;
        _commandAppService = commandAppService;
        _logger = logger;
        _inputService = inputService;
        _sessionManager = sessionManager;
        _dataService = dataService;
        _outputService = outputService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting Telegram polling");

        await CleanupStaleMessagesAsync(stoppingToken);
        await BotCommandsSetup.ConfigureAsync(_botClient, _logger);

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery]
        };

        var updateHandler = new DefaultUpdateHandler(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandleErrorAsync
        );

        _botClient.StartReceiving(updateHandler, receiverOptions, stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            // Expected when the host is stopping
        }

        _logger.LogInformation("Stopping Telegram polling");
    }

    public async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken token)
    {
        try
        {
            var dto = await _inputService.Map(update);

            switch (dto)
            {
                case MessageDto message:
                    using (await _sessionManager.AcquireUserLockAsync(message.UserId))
                    {
                        _ = _sessionManager.GetOrCreateSession(message.UserId);

                        if (message.Text == null)
                        {
                            _logger.LogWarning("Received message with null Text from {Username} ({UserId})", message.Username, message.UserId);
                            return;
                        }

                        await _commandAppService.HandleUserCommandAsync(message, token);
                    }
                    break;
                case CallbackQueryDto callback:
                    if (callback.CallbackQueryId == null)
                    {
                        _logger.LogWarning("Received callback with null CallbackQueryId from {Username} ({UserId})", callback.Username, callback.UserId);
                        return;
                    }
                    using (await _sessionManager.AcquireUserLockAsync(callback.UserId))
                    {
                        _ = _sessionManager.GetOrCreateSession(callback.UserId);
                        await _commandAppService.HandleCallbackAsync(callback, token);
                        await bot.AnswerCallbackQuery(callback.CallbackQueryId, cancellationToken: token);
                    }
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Update handling was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception processing update {UpdateId}", update.Id);
        }
    }

    private async Task CleanupStaleMessagesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var staleMessages = await _dataService.GetAllTrackedMessagesAsync();
            foreach (var group in staleMessages)
            {
                await _outputService.DeleteMessagesAsync(group.Key, group, cancellationToken);
                await _dataService.DeleteTrackedMessagesAsync(group.Key);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup stale messages on startup");
        }
    }

    private Task HandleErrorAsync(ITelegramBotClient client, Exception exception, CancellationToken token)
    {
        _logger.LogError(exception, "Polling error");
        return Task.CompletedTask;
    }
}
