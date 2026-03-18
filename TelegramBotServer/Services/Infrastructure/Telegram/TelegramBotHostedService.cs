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
                        _ = _sessionManager.GetOrCreateSession(callback.UserId);
                        if (!await CallbackAuthorization(callback.UserId))
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
        // Lockout check: applies to all paths except /start or /auth (handled separately below)
        if (session.State == SessionState.WaitingForPassword
            && session.FailedAuthAttempts >= UserSession.MaxFailedAttempts
            && text != "/auth" && text != "/start")
        {
            _logger.LogWarning("User {UserId} is locked out after {Attempts} failed attempts", userId, session.FailedAuthAttempts);
            await _outputService.SendMessageAsync(userId, $"Слишком много неверных попыток ({UserSession.MaxFailedAttempts}). Попробуйте позже или обратитесь к администратору.");
            return false;
        }

        if (text == "/auth" || text == "/start")
        {
            var checkAuth = await _authService.CheckAuthAsync(userId);
            if (!checkAuth)
            {
                session.State = SessionState.WaitingForPassword;
                var welcomeMessage = text == "/start"
                    ? "Привет! Я бот для работы с BIM-документами, автоматизации задач и экспорта файлов.\nДля работы со мной нужна авторизация. Пожалуйста, введите пароль:"
                    : "Введите пароль:";
                await _outputService.SendMessageAsync(userId, welcomeMessage);
                return false;
            }

            if (text == "/auth")
            {
                await _outputService.SendMessageAsync(userId, "Вы уже авторизованы.");
                return true;
            }

            // /start для авторизованного пользователя — пропускаем в CommandAppService.
            return true;
        }
        else if (session.State == SessionState.WaitingForPassword)
        {
            bool auth = await _authService.AuthorizeUserAsync(userId, username, text);
            if (!auth)
            {
                session.FailedAuthAttempts++;
                int remaining = UserSession.MaxFailedAttempts - session.FailedAuthAttempts;

                _logger.LogWarning("User {UserId} failed auth attempt {Attempts}/{Max}", userId, session.FailedAuthAttempts, UserSession.MaxFailedAttempts);

                var failMessage = remaining > 0
                    ? $"Неверный пароль. Осталось попыток: {remaining}."
                    : $"Неверный пароль. Вы заблокированы после {UserSession.MaxFailedAttempts} неудачных попыток.";

                await _outputService.SendMessageAsync(userId, failMessage);
                return false;
            }

            // Successful auth: reset counter and state
            session.FailedAuthAttempts = 0;
            session.State = SessionState.Idle;
            await _outputService.SendMessageAsync(userId, "Авторизация успешна. Нажмите /start для вызова главного меню.");

            return false;
        }
        else
        {
            var checkAuth = await _authService.CheckAuthAsync(userId);
            if (!checkAuth)
            {
                await _outputService.SendMessageAsync(userId, "Вы не авторизованы. Введите /start или /auth для авторизации.");
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
            await _outputService.SendMessageAsync(userId, "Вы не авторизованы. Введите /start или /auth для авторизации.");
            return false;
        }

        return true;
    }


}