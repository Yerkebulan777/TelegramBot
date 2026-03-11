using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace TelegramBotServer.Interfaces;

public interface ITelegramOutputService
{
    /// <summary>
    /// Sends a standard message to a user.
    /// </summary>
    Task<Message> SendMessageAsync(long userId, string message);

    /// <summary>
    /// Sends an error message (??) to a user.
    /// </summary>
    Task SendErrorAsync(long userId, string errorMessage);

    /// <summary>
    /// Sends a system notification (for example, to admin or log channel).
    /// </summary>
    Task SendNotificationAsync(string message);


    Task SendMessageWithKeyboardAsync(long userId, string message, InlineKeyboardMarkup keyboard);

    Task DeleteMessageAsync(long chatId, int messageId);


    Task AnswerCallbackAsync(string callbackId, string messageText);

    Task EditMessageReplyMarkupAsync(long userId, int messageId, InlineKeyboardMarkup keyboard);
    Task EditMessageReplyTextAsync(long userId, int messageId, string message);
}