using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Models;

namespace TelegramBot.Server.Interfaces;

public interface ITelegramOutputService
{
    Task<Message?> SendMessageAsync(long userId, string message);
    Task<Message?> SendMessageWithKeyboardAsync(long userId, string message, InlineKeyboardMarkup keyboard);
    Task<Message?> SendMessageWithReplyKeyboardAsync(long userId, string message, ReplyKeyboardMarkup keyboard);
    Task<Message?> RemoveReplyKeyboardAsync(long userId, string message);
    Task DeleteMessageAsync(long chatId, int messageId);
    Task DeleteMessagesAsync(long chatId, IEnumerable<int> messageIds, CancellationToken cancellationToken = default);
    Task ClearChatHistoryAsync(long chatId, UserSession session, IEnumerable<int>? exceptMessageIds = null);
    Task CleanupTrackedMessagesAsync(long chatId, UserSession session, IEnumerable<int> keepMessageIds, CancellationToken cancellationToken = default);
    Task AnswerCallbackAsync(string callbackId, string messageText);
    Task EditMessageReplyMarkupAsync(long userId, int messageId, InlineKeyboardMarkup keyboard);
    Task EditMessageReplyTextAsync(long userId, int messageId, string message);
    Task EditMessageTextWithKeyboardAsync(long userId, int messageId, string message, InlineKeyboardMarkup keyboard);
}
