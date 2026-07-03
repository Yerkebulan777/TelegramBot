using Telegram.Bot.Types;
using TelegramBot.Data;

namespace TelegramBot.Core.Services;

/// <summary>
/// Сервис для трекинга сообщений Telegram с сессией.
/// Устраняет дублирование TrackMessageAsync в SlashCommandService и handlers.
/// </summary>
public sealed class MessageTrackingService(MessageTrackingDataService dataService, ILogger<MessageTrackingService> logger)
{
    /// <summary>
    /// Трекает сообщение и возвращает его ID.
    /// </summary>
    public async Task<int?> TrackAsync(long chatId, int messageId, int? sessionId)
    {
        try
        {
            await dataService.TrackMessageAsync(chatId, messageId, sessionId);
            return messageId;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to track message: chatId={ChatId}, messageId={MessageId}", chatId, messageId);
            return null;
        }
    }

    /// <summary>
    /// Трекает сообщение из Task<Message?>.
    /// </summary>
    public async Task<int?> TrackAsync(Task<Message?> messageTask, int? sessionId)
    {
#pragma warning disable VSTHRD003
        var msg = await messageTask;
#pragma warning restore VSTHRD003

        if (msg == null)
        {
            return null;
        }

        return await TrackAsync(msg.Chat.Id, msg.MessageId, sessionId);
    }
}
