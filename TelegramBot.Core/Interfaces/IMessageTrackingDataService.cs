namespace TelegramBot.Core.Interfaces;

/// <summary>
/// Service for message tracking persistence.
/// </summary>
public interface IMessageTrackingDataService
{
    /// <summary>Трекит сообщение.</summary>
    Task TrackMessageAsync(long chatId, int messageId, int? sessionId = null);

    /// <summary>Удаляет трекированные сообщения по ID.</summary>
    Task DeleteTrackedMessagesAsync(int sessionId, IEnumerable<int> messageIds);

    /// <summary>Удаляет все трекированные сообщения сессии.</summary>
    Task DeleteTrackedMessagesBySessionAsync(int sessionId);

    /// <summary>Удаляет трекированные сообщения чата.</summary>
    Task DeleteTrackedMessagesByChatAsync(long chatId, IEnumerable<int> messageIds);

    /// <summary>Возвращает трекированные сообщения сессии.</summary>
    Task<IReadOnlyList<int>> GetTrackedMessagesBySessionAsync(int sessionId);

    /// <summary>Возвращает трекированные сообщения чата.</summary>
    Task<IReadOnlyList<int>> GetTrackedMessagesByChatAsync(long chatId);
}
