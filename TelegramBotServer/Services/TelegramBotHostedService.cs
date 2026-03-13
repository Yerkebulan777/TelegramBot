using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramBotServer.DTOs;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services;

public class TelegramBotHostedService : BackgroundService
{
    private readonly ITelegramBotClient _botClient;
    private readonly ICommandAppService _commandAppService;
    private readonly ILogger<TelegramBotHostedService> _logger;
    private readonly ITelegramUpdateMapper _inputService;
    private readonly ISessionManager _sessionManager;
    private readonly IAuthService _authService;
    private readonly ITelegramOutputService _outputService;

    public TelegramBotHostedService(
        ITelegramBotClient botClient,
        ICommandAppService commandAppService,
        ILogger<TelegramBotHostedService> logger,
        ITelegramUpdateMapper inputService,
        ISessionManager sessionManager,
        IAuthService authService,
        ITelegramOutputService outputService)
    {
        _botClient = botClient;
        _commandAppService = commandAppService;
        _logger = logger;
        _inputService = inputService;
        _sessionManager = sessionManager;
        _authService = authService;
        _outputService = outputService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting Telegram polling");

        await Config.Config.ConfigureAsync(_botClient);


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

        var dto = await _inputService.Map(update);
        
        switch (dto)
        {
            case MessageDto message:
                using (await _sessionManager.AcquireUserLockAsync(message.UserId))
                {
                    var session = _sessionManager.GetOrCreateSession(message.UserId);

                    if (message.Username == null||message.Text==null)
                        throw new InvalidOperationException("message.Username or message.Text is null.");

                    if (!await Authorization(message.UserId, message.Username, message.Text, session))
                        return;
                    await _commandAppService.HandleUserCommandAsync(message);
                }
                break;
            case CallbackQueryDto callback:
                using (await _sessionManager.AcquireUserLockAsync(callback.UserId))
                {
                    var cbSession = _sessionManager.GetOrCreateSession(callback.UserId);
                    if (!await CallbackAuthorization(callback.UserId))
                        return;

                    await _commandAppService.HandleCallbackAsync(callback);

                    if (callback.CallbackQueryId == null)
                        throw new InvalidOperationException("callback.CallbackQueryId is null.");

                    await bot.AnswerCallbackQuery(callback.CallbackQueryId, callback.CallbackData, cancellationToken: token);
                }
                break;
        }
    }

    private Task HandleErrorAsync(ITelegramBotClient client, Exception exception, CancellationToken token)
    {
        _logger.LogError(exception, "Polling error");
        return Task.CompletedTask;
    }


    private async Task<bool> Authorization(long userId, string username, string text, UserSession session)
    {
        if (text == "/auth")
        {
            var checkAuth = await _authService.CheckAuthAsync(userId);
            if (!checkAuth)
            {
                session.State = "WaitingForPassword";
                await _outputService.SendMessageAsync(userId, "Enter password.");
                return false;
            }
            await _outputService.SendMessageAsync(userId, "Already authorized. Proceeding.");
            
            return true;
        }
        else if (session.State == "WaitingForPassword")
        {
            bool auth = await _authService.AuthorizeUserAsync(userId, username, text);
            if (!auth)
            {
                session.State = "WaitingForPassword";
                await _outputService.SendMessageAsync(userId, "Incorrect password. Try again.");
                return false;
            }
            session.State = "Idle";
            await _outputService.SendMessageAsync(userId, "Successfully authorized.");
            
            return true;
        }
        else
        {
            var checkAuth = await _authService.CheckAuthAsync(userId);
            if (!checkAuth)
            {
                await _outputService.SendMessageAsync(userId, "Not authorized. Enter select or enter /auth to authorize.");
                return false;
            }
            
            return true;
        }
    }

    private async Task<bool> CallbackAuthorization(long userId)
    {
        var checkAuth = await _authService.CheckAuthAsync(userId);
        if (!checkAuth)
        {
            await _outputService.SendMessageAsync(userId, "Not authorized. Enter select or enter /auth to authorize.");
            return false;
        }

        return true;
    }


}