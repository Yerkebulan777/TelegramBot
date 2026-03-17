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
                        var session = _sessionManager.GetOrCreateSession(message.UserId);

                        if (message.Username == null || message.Text == null)
                        {
                            _logger.LogWarning("Received message with null Username or Text from {UserId}", message.UserId);
                            return;
                        }

                        if (!await Authorization(message.UserId, message.Username, message.Text, session))
                            return;
                        await _commandAppService.HandleUserCommandAsync(message, token);
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
                        var session = _sessionManager.GetOrCreateSession(callback.UserId);
                        if (!await CallbackAuthorization(callback.UserId, session))
                            return;

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


    private async Task<bool> Authorization(long userId, string username, string text, UserSession session)
    {
        if (text == "/auth" || text == "/start")
        {
            var checkAuth = session.IsAuthorized || await _authService.CheckAuthAsync(userId);
            if (!checkAuth)
            {
                session.State = SessionState.WaitingForPassword;
                var welcomeMessage = text == "/start"
                    ? "Привет! Я бот для работы с BIM-документами, автоматизации задач и экспорта файлов.\nДля работы со мной нужна авторизация. Пожалуйста, введите пароль:"
                    : "Enter password.";
                await _outputService.SendMessageAsync(userId, welcomeMessage);
                return false;
            }

            session.IsAuthorized = true;

            if (text == "/auth")
            {
                await _outputService.SendMessageAsync(userId, "Already authorized. Proceeding.");
                return true;
            }

            // Если это /start и пользователь авторизован, пропускаем команду дальше 
            // в CommandAppService для показа главного меню.
            return true;
        }
        else if (session.State == SessionState.WaitingForPassword)
        {
            bool auth = await _authService.AuthorizeUserAsync(userId, username, text);
            if (!auth)
            {
                session.State = SessionState.WaitingForPassword;
                await _outputService.SendMessageAsync(userId, "Incorrect password. Try again.");
                return false;
            }
            session.State = SessionState.Idle;
            session.IsAuthorized = true;
            await _outputService.SendMessageAsync(userId, "Successfully authorized.");

            // После успешной авторизации мы не пропускаем введенный пароль дальше как команду.
            // Пользователь должен будет нажать /start (или мы можем вызвать это меню сами, но проще попросить).
            // Или можно автоматически показать меню. Пока просто возвращаем false (не команда).
            // Но чтобы было красивее, мы можем сами показать меню или сказать нажать /start.
            // При возврате false сообщение "Successfully authorized" - последнее. 
            // Мы попросим пользователя нажать /start.
            await _outputService.SendMessageAsync(userId, "Нажмите /start для вызова главного меню.");

            return false;
        }
        else
        {
            var checkAuth = session.IsAuthorized || await _authService.CheckAuthAsync(userId);
            if (!checkAuth)
            {
                await _outputService.SendMessageAsync(userId, "Not authorized. Enter /start or /auth to authorize.");
                return false;
            }

            session.IsAuthorized = true;
            return true;
        }
    }

    private async Task<bool> CallbackAuthorization(long userId, UserSession session)
    {
        var checkAuth = session.IsAuthorized || await _authService.CheckAuthAsync(userId);
        if (!checkAuth)
        {
            await _outputService.SendMessageAsync(userId, "Not authorized. Enter select or enter /auth to authorize.");
            return false;
        }

        session.IsAuthorized = true;
        return true;
    }


}