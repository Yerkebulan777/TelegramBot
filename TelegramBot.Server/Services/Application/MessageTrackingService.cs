using Telegram.Bot.Types;
using TelegramBot.Core.Models;
using TelegramBot.Data;

namespace TelegramBot.Server.Services.Application;

public sealed class MessageTrackingService(MessageTrackingDataService messageTrackingDataService)
{
    public async Task<Message?> TrackAsync(Task<Message?> messageTask, UserSession session)
    {
#pragma warning disable VSTHRD003 // Foreign Task passed as parameter — intentionally awaited here
        return await TrackAsync(await messageTask, session);
#pragma warning restore VSTHRD003
    }

    public async Task<Message?> TrackAsync(Message? message, UserSession session)
    {
        if (message != null)
        {
            var sessionId = session.SessionId > 0 ? session.SessionId : (int?)null;
            await messageTrackingDataService.TrackMessageAsync(message.Chat.Id, message.MessageId, sessionId);
        }

        return message;
    }
}
