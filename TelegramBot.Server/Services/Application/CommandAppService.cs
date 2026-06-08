using TelegramBot.Core.DTOs;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;
using TelegramBot.Core.Services;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Application;

public sealed class CommandAppService(
    ISessionManager sessionManager,
    ICallbackDispatcher callbackDispatcher,
    ISlashCommandService slashCommandService,
    ITelegramOutputService outputService,
    RateLimiter rateLimiter,
    ILogger<CommandAppService> logger) : ICommandAppService
{
    public async Task HandleUserCommandAsync(MessageDto message, CancellationToken cancellationToken = default)
    {
        if (!rateLimiter.IsAllowed(message.UserId))
        {
            await outputService.SendMessageAsync(message.UserId,
                "⚠️ Слишком много запросов. Пожалуйста, подождите немного.");
            return;
        }

        var session = sessionManager.GetOrCreateSession(message.UserId);

        // Server restart detection: session has no tracked messages (fresh after restart)
        // and user sends a non-slash text — redirect to /start for a clean slate
        if (session.SessionId <= 0 && !message.Text!.StartsWith('/'))
        {
            logger.LogDebug("Post-restart cleanup for {Username} ({UserId}): redirecting to /start",
                message.Username, message.UserId);
            await outputService.RemoveReplyKeyboardAsync(message.UserId,
                "⚡️ Сервер был перезапущен.\nСтарые сообщения больше неактуальны.\n\nИспользуйте /start для начала.");
            return;
        }

        await slashCommandService.HandleUserCommandAsync(message, session, cancellationToken);
    }

    public async Task HandleCallbackAsync(CallbackQueryDto callback, CancellationToken cancellationToken = default)
    {
        if (callback.Username == null || callback.MessageText == null || callback.CallbackData == null || callback.CallbackQueryId == null)
        {
            logger.LogWarning("Received incomplete callback from user {UserId}", callback.UserId);
            return;
        }

        var session = sessionManager.GetOrCreateSession(callback.UserId);
        var parsed = CallbackDataParser.Parse(callback.CallbackData);

        var isRegistrationCallback = parsed.Prefix is
            CallbackPrefixes.RequestAccess or
            CallbackPrefixes.ApproveUser or
            CallbackPrefixes.RejectUser;

        if (!isRegistrationCallback && !await slashCommandService.CheckAndNotifyAccessAsync(callback.UserId, session))
        {
            logger.LogWarning("Callback rejected: prefix={Prefix}, user={UserId}, reason=access_denied", parsed.Prefix, callback.UserId);
            return;
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

    private static HashSet<int> GetProtectedMessageIds(MessageDto message, UserSession session)
    {
        return new int?[]
            {
                message.MessageId,
                session.CommandSelectionMessageId,
                session.FileSelectionMessageId,
                session.StatusMessageId
            }
            .Where(messageId => messageId.HasValue)
            .Select(messageId => messageId!.Value)
            .ToHashSet();
    }
}
