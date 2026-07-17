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
        await messageTrackingService.TrackAsync(
            message.ChatId == 0 ? message.UserId : message.ChatId,
            message.MessageId,
            session);

        // Stale-session detection: a fresh session (after restart, idle timeout, or first run)
        // is not yet initialized. A slash command initializes it; any other text gets a single
        // redirect-to-/start hint. Initialized is then set unconditionally — true is idempotent
        // for already-initialized sessions.
        if (!session.Initialized && !message.Text!.StartsWith('/'))
        {
            await NotifyStaleSessionAsync(session, message.UserId, message.Username);
            return;
        }
        session.Initialized = true;

        await slashCommandService.HandleUserCommandAsync(message, session, cancellationToken);
    }

    public async Task HandleCallbackAsync(CallbackQueryDto callback, CancellationToken cancellationToken = default)
    {
        if (!rateLimiter.IsAllowed(callback.UserId))
        {
            _ = await outputService.SendMessageAsync(callback.UserId,
                "⚠️ Слишком много запросов. Пожалуйста, подождите немного.");
            return;
        }

        if (callback.Username == null || callback.MessageText == null || callback.CallbackData == null || callback.CallbackQueryId == null)
        {
            logger.LogWarning("Incomplete callback: user={UserId}", callback.UserId);
            return;
        }

        var session = sessionManager.GetOrCreateSession(callback.UserId);
        var parsed = CallbackDataParser.Parse(callback.CallbackData);
        var bypassesAccess = accessValidator.BypassesAccessCheck(parsed.Prefix);

        // Stale-session detection for callbacks. Bootstrap callbacks (REQACCESS, APPROVEUSER,
        // REJECTUSER) operate on DB state, not session state, so they bypass this check — same
        // exemption as the access check below. All other callbacks depend on session-local state
        // (pending commands, file selection, status filters) that no longer exists after restart,
        // so a fresh session is redirected to /start via a single hint.
        if (!session.Initialized && !bypassesAccess)
        {
            await NotifyStaleSessionAsync(session, callback.UserId, callback.Username);
            return;
        }
        session.Initialized = true;

        if (!bypassesAccess)
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

    /// <summary>
    /// Notifies the user that their previous session is gone (server restart, idle timeout, or
    /// first run) and they should start over. Removes any lingering reply keyboard, tracks the
    /// sent message so it is cleaned up by ClearChatHistoryAsync, and marks the session
    /// initialized so the hint fires at most once per session.
    /// </summary>
    private async Task NotifyStaleSessionAsync(UserSession session, long userId, string? username)
    {
        logger.LogDebug("Stale session redirected to /start: {Username} ({UserId})", username, userId);
        _ = await messageTrackingService.TrackAsync(
            outputService.RemoveReplyKeyboardAsync(userId,
                "⚡️ Сервер перезапущен или сессия истекла.\nСтарые сообщения неактуальны.\n\nВведите /start."),
            session);
        session.Initialized = true;
    }

}
