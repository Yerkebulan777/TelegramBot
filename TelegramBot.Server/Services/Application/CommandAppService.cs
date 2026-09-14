using Telegram.Bot.Types;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Helpers;
using TelegramBot.Core.Models;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Services.Application;

public sealed class CommandAppService(
    TelegramOutputService outputService,
    CallbackDispatcher callbackDispatcher,
    SlashCommandService slashCommandService,
    SessionManager sessionManager,
    MessageTrackingService messageTrackingService,
    RateLimiter rateLimiter,
    ILogger<CommandAppService> logger)
{
    private const string AnonymousProfileMessage =
        "Ваш профиль анонимен. Укажите имя или @username в настройках Telegram, чтобы пользоваться ботом.";

    public async Task HandleUserCommandAsync(Message message, CancellationToken cancellationToken = default)
    {
        var sender = message.From
            ?? throw new InvalidOperationException("Message.From is null.");
        var userId = sender.Id;
        var username = sender.Username ?? sender.FirstName;

        // Decide admission before tracking performs any database I/O.
        var admitted = await TryAdmitAsync(userId, username);

        var session = sessionManager.GetOrCreateSession(userId);

        // Track the user's own message so it can be deleted on the next slash command
        session.LastUserMessageId = message.MessageId;
        await messageTrackingService.TrackAsync(
            message.Chat.Id,
            message.MessageId,
            session,
            message.Date);

        if (!admitted)
        {
            return;
        }

        // Stale-session detection: a fresh session (after restart, idle timeout, or first run)
        // is not yet initialized. A slash command initializes it; any other text gets a
        // redirect-to-/start hint until the user sends /start.
        if (!session.Initialized && !message.Text!.StartsWith('/'))
        {
            await NotifyStaleSessionAsync(session, userId, username, cancellationToken);
            return;
        }
        session.Initialized = true;

        await outputService.DeleteTemporaryMessagesAsync(userId, cancellationToken);
        await slashCommandService.HandleUserCommandAsync(message, session, cancellationToken);
    }

    public async Task HandleCallbackAsync(CallbackQuery callback, CancellationToken cancellationToken = default)
    {
        CallbackContext? context = null;
        try
        {
            var userId = callback.From.Id;
            var username = callback.From.Username ?? callback.From.FirstName;

            if (!await TryAdmitAsync(userId, username))
            {
                return;
            }

            if (callback.Message?.Text == null || callback.Data == null)
            {
                logger.LogWarning("Incomplete callback: user={UserId}", userId);
                return;
            }

            var session = sessionManager.GetOrCreateSession(userId);
            var parsed = ParsedCallback.Parse(callback.Data);

            // Stale-session detection for callbacks. Callbacks depend on session-local state
            // (pending commands, file selection, status filters) that no longer exists after restart,
            // so a fresh session is redirected to /start via a single hint.
            if (!session.Initialized)
            {
                await NotifyStaleSessionAsync(session, userId, username, cancellationToken);
                return;
            }

            await outputService.DeleteTemporaryMessagesAsync(userId, cancellationToken);

            context = new CallbackContext
            {
                UserId = userId,
                MessageId = callback.Message.MessageId,
                Username = username!, // non-null: TryAdmitAsync rejected blank usernames above
                CallbackQueryId = callback.Id,
                ParsedCallback = parsed,
                Session = session
            };

            try
            {
                await callbackDispatcher.DispatchAsync(context, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Callback handler error: prefix={Prefix}", parsed.Prefix);
                await outputService.AnswerCallbackAsync(context, "⚠️ Не удалось обработать действие.");
            }
        }
        finally
        {
            if (context?.CallbackAnswered != true)
            {
                await outputService.AnswerCallbackAsync(callback.Id, "");
            }
        }
    }

    /// <summary>Общие входные гейты для сообщений и коллбэков: rate limit, затем анонимность.</summary>
    private async Task<bool> TryAdmitAsync(long userId, string? username)
    {
        if (!rateLimiter.IsAllowed(userId, out var shouldNotify))
        {
            if (shouldNotify)
            {
                _ = await messageTrackingService.TrackAsync(
                    outputService.SendMessageAsync(userId,
                        "⚠️ Слишком много запросов. Пожалуйста, подождите немного."),
                    sessionManager.GetOrCreateSession(userId), TrackedMessageKinds.Temporary);
            }
            return false;
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            await RejectAnonymousAsync(userId);
            return false;
        }

        return true;
    }

    /// <summary>Отшивает безликий аккаунт: логирует и шлёт инструкцию заполнить профиль.</summary>
    private async Task RejectAnonymousAsync(long userId)
    {
        logger.LogWarning("Anonymous user rejected: {UserId}", userId);
        _ = await messageTrackingService.TrackAsync(
            outputService.SendMessageAsync(userId, AnonymousProfileMessage),
            sessionManager.GetOrCreateSession(userId), TrackedMessageKinds.Temporary);
    }

    /// <summary>
    /// Notifies the user that their previous session is gone (server restart, idle timeout, or
    /// first run) and they should start over. Removes any lingering reply keyboard and tracks the
    /// sent message so it is cleaned up by ClearChatHistoryAsync. Does not mark the session
    /// initialized — that happens only on a slash command such as /start.
    /// </summary>
    private async Task NotifyStaleSessionAsync(
        UserSession session, long userId, string? username, CancellationToken cancellationToken)
    {
        logger.LogDebug("Stale session redirected to /start: {Username} ({UserId})", username, userId);
        _ = await messageTrackingService.TrackAsync(
            outputService.RemoveReplyKeyboardAsync(userId,
                "⚡️ Сервер перезапущен или сессия истекла.\nСтарые сообщения неактуальны.\n\nВведите /start.",
                cancellationToken),
            session, TrackedMessageKinds.Temporary);
    }

}
