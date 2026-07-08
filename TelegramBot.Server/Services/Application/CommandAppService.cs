using TelegramBot.Core.DTOs;
using TelegramBot.Core.Helpers;
using TelegramBot.Core.Models;
using TelegramBot.Server.Middleware;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Services.Application;

public sealed class CommandAppService(
    TelegramOutputService outputService,
    AuthorizationMiddleware accessValidator,
    CallbackDispatcher callbackDispatcher,
    SlashCommandService slashCommandService,
    SessionManager sessionManager,
    MessageTrackingService messageTrackingService,
    RateLimiter rateLimiter,
    ILogger<CommandAppService> logger)
{
    public async Task HandleUserCommandAsync(MessageDto message, CancellationToken cancellationToken = default)
    {
        if (!rateLimiter.IsAllowed(message.UserId))
        {
            _=await outputService.SendMessageAsync(message.UserId,
                "⚠️ Слишком много запросов. Пожалуйста, подождите немного.");
            return;
        }

        var session = sessionManager.GetOrCreateSession(message.UserId);

        // Track the user's own message so it can be deleted on the next slash command
        session.LastUserMessageId = message.MessageId;

        // Server restart detection: session is fresh after restart (not yet initialized)
        // and user sends a non-slash text — redirect to /start for a clean slate
        if (!session.Initialized && !message.Text!.StartsWith('/'))
        {
            logger.LogDebug("Post-restart redirect to /start: {Username} ({UserId})",
                message.Username, message.UserId);
            _=await outputService.RemoveReplyKeyboardAsync(message.UserId,
                "⚡️ Сервер был перезапущен.\nСтарые сообщения больше неактуальны.\n\nИспользуйте /start для начала.");
            return;
        }

        // Any slash command marks the session as initialized (after restart or fresh start)
        if (message.Text!.StartsWith('/'))
        {
            session.Initialized = true;
        }

        await slashCommandService.HandleUserCommandAsync(message, session, cancellationToken);
    }

    public async Task HandleCallbackAsync(CallbackQueryDto callback, CancellationToken cancellationToken = default)
    {
        if (callback.Username == null || callback.MessageText == null || callback.CallbackData == null || callback.CallbackQueryId == null)
        {
            logger.LogWarning("Incomplete callback: user={UserId}", callback.UserId);
            return;
        }

        var session = sessionManager.GetOrCreateSession(callback.UserId);
        var parsed = CallbackDataParser.Parse(callback.CallbackData);

        if (!accessValidator.BypassesAccessCheck(parsed.Prefix))
        {
            var access = await accessValidator.ValidateAsync(callback.UserId);
            if (!access.IsActive)
            {
                _ = await messageTrackingService.TrackAsync(
                    outputService.SendMessageAsync(callback.UserId, "У вас нет доступа. Введите /start для запроса доступа."),
                    session);

                logger.LogWarning("Callback rejected: prefix={Prefix}, user={Username} ({UserId}), reason=access", parsed.Prefix, callback.Username, callback.UserId);
                return;
            }
        }

        var context = new CallbackContext
        {
            UserId = callback.UserId,
            ChatId = callback.ChatId,
            MessageId = callback.MessageId,
            Username = callback.Username,
            CallbackQueryId = callback.CallbackQueryId,
            ParsedCallback = parsed,
            Session = session
        };

        await callbackDispatcher.DispatchAsync(context, cancellationToken);
    }

}
