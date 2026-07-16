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
            await TrackAsync(message.Chat.Id, message.MessageId, session);
        }

        return message;
    }

    public async Task TrackAsync(long chatId, int messageId, UserSession session)
    {
        var sessionId = session.SessionId > 0 ? session.SessionId : (int?)null;
        await messageTrackingDataService.TrackMessageAsync(chatId, messageId, sessionId);
    }

    /// <summary>
    /// Регистрирует сообщение, отправленное вне интерактивной сессии (уведомления Worker/Revit),
    /// привязкой к <paramref name="sessionId"/> для последующей очистки чата.
    /// </summary>
    public async Task TrackAsync(Message? message, int sessionId)
    {
        if (message != null)
        {
            await messageTrackingDataService.TrackMessageAsync(message.Chat.Id, message.MessageId, sessionId);
        }
    }
}
