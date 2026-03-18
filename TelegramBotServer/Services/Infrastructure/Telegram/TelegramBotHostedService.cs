using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramBotServer.DTOs;
using TelegramBotServer.Interfaces;

namespace TelegramBotServer.Services;

public class TelegramBotHostedService : BackgroundService
{
    private readonly ITelegramBotClient _botClient;
    private readonly ICommandAppService _commandAppService;
    private readonly ILogger<TelegramBotHostedService> _logger;
    private readonly ITelegramUpdateMapper _inputService;
    private readonly ISessionManager _sessionManager;
    private readonly ITelegramOutputService _outputService;

    public TelegramBotHostedService(
        ITelegramBotClient botClient,
        ICommandAppService commandAppService,
        ILogger<TelegramBotHostedService> logger,
        ITelegramUpdateMapper inputService,
        ISessionManager sessionManager,
        ITelegramOutputService outputService)
    {
        _botClient = botClient;
        _commandAppService = commandAppService;
        _logger = logger;
        _inputService = inputService;
        _sessionManager = sessionManager;
        _outputService = outputService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting Telegram polling");

        await Config.Config.ConfigureAsync(_botClient, _logger);


        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = new[] { UpdateType.Message, UpdateType.CallbackQuery }
        };

        // Create the DefaultUpdateHandler with proper delegates
        var updateHandler = new DefaultUpdateHandler(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandleErrorAsync
        );

        _botClient.StartReceiving(
            updateHandler,       // IUpdateHandler
            receiverOptions,     // ReceiverOptions
            stoppingToken        // CancellationToken
        );

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
                        bool shouldDeleteCommandMessage = IsSlashCommandMessage(message.Text);
                        _ = _sessionManager.GetOrCreateSession(message.UserId);

                        if (message.Text == null)
                        {
                            _logger.LogWarning("Received message with null Text from {UserId}", message.UserId);
                            return;
                        }

                        await _commandAppService.HandleUserCommandAsync(message, token);

                        if (shouldDeleteCommandMessage)
                        {
                            await _outputService.DeleteMessageAsync(message.ChatId, message.MessageId);
                        }
                    }
                    break;
                case CallbackQueryDto callback:
                    if (callback.CallbackQueryId == null)
                    {
                        _logger.LogWarning("Received callback with null CallbackQueryId from {UserId}", callback.UserId);
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
            // Expected during shutdown
            _logger.LogDebug("Update handling was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception processing update {UpdateId}", update.Id);
        }
    }

    private Task HandleErrorAsync(ITelegramBotClient client, Exception exception, CancellationToken token)
    {
        _logger.LogError(exception, "Polling error");
        return Task.CompletedTask;
    }

    private static bool IsSlashCommandMessage(string? text)
    {
        return !string.IsNullOrWhiteSpace(text) && text.StartsWith('/');
    }
}
