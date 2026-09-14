using Microsoft.Extensions.Options;
using Telegram.Bot.Types;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Data;

namespace TelegramBot.Server.Services.Application;

public sealed class MessageTrackingService(
    MessageTrackingDataService messageTrackingDataService,
    IOptions<MessageCleanupOptions> cleanupOptions)
{
    public async Task<Message?> TrackAsync(Task<Message?> messageTask, UserSession session,
        string kind = TrackedMessageKinds.Interface)
    {
#pragma warning disable VSTHRD003 // Foreign Task passed as parameter — intentionally awaited here
        return await TrackAsync(await messageTask, session, kind);
#pragma warning restore VSTHRD003
    }

    public async Task<Message?> TrackAsync(Message? message, UserSession session,
        string kind = TrackedMessageKinds.Interface)
    {
        if (message != null)
        {
            await TrackAsync(message.Chat.Id, message.MessageId, session, message.Date, kind);
        }

        return message;
    }

    public async Task TrackAsync(long chatId, int messageId, UserSession session, DateTime? sentAt = null,
        string kind = TrackedMessageKinds.Interface)
    {
        var sessionId = session.SessionId > 0 ? session.SessionId : (int?)null;
        var now = DateTime.UtcNow;
        var createdAt = sentAt ?? now;
        var deleteAfter = kind == TrackedMessageKinds.Temporary
            ? now.AddMinutes(cleanupOptions.Value.TemporaryRetentionMinutes)
            : now.AddHours(cleanupOptions.Value.RetentionHours);
        await messageTrackingDataService.TrackMessageAsync(chatId, messageId, sessionId,
            kind, createdAt, deleteAfter);
    }

}
