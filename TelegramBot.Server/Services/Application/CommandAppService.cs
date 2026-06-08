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
        var cleanupCandidates = session.GetTrackedMessages();

        session.TrackMessage(message.MessageId);
        await slashCommandService.HandleUserCommandAsync(message, session, cancellationToken);
        await DeleteOldMessagesAsync(message, session, cleanupCandidates, cancellationToken);
    }

    public async Task HandleCallbackAsync(CallbackQueryDto callback, CancellationToken cancellationToken = default)
    {
        if (callback.Username == null || callback.MessageText == null || callback.CallbackData == null || callback.CallbackQueryId == null)
        {
            logger.LogWarning("Received incomplete callback from user {UserId}", callback.UserId);
            return;
        }

        var session = sessionManager.GetOrCreateSession(callback.UserId);
        session.TrackMessage(callback.MessageId);
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

    private async Task DeleteOldMessagesAsync(
        MessageDto message,
        UserSession session,
        IReadOnlyCollection<int> cleanupCandidates,
        CancellationToken cancellationToken)
    {
        if (cleanupCandidates.Count == 0)
        {
            return;
        }

        var protectedMessageIds = GetProtectedMessageIds(message, session);
        var messageIds = cleanupCandidates
            .Where(messageId => !protectedMessageIds.Contains(messageId))
            .ToArray();

        if (messageIds.Length == 0)
        {
            return;
        }

        try
        {
            await outputService.DeleteMessagesAsync(message.UserId, messageIds, cancellationToken);
        }
        finally
        {
            session.UntrackMessages(messageIds);
        }
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
