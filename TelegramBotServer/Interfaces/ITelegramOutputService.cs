using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace TelegramBotServer.Interfaces;

public interface ITelegramOutputService
{
    /// <summary>
    /// Sends a standard message to a user.
    /// </summary>
    Task<Message?> SendMessageAsync(long userId, string message);

    /// <summary>
    /// Sends an error message (??) to a user.
    /// </summary>
    Task SendErrorAsync(long userId, string errorMessage);

    /// <summary>
    /// Sends a system notification (for example, to admin or log channel).
    /// </summary>
    Task SendNotificationAsync(string message);


    /// <summary>
    /// Sends a message with an inline keyboard to a user.
    /// </summary>
    Task SendMessageWithKeyboardAsync(long userId, string message, InlineKeyboardMarkup keyboard);

    /// <summary>
    /// Sends a message with a reply keyboard to a user.
    /// </summary>
    Task SendMessageWithReplyKeyboardAsync(long userId, string message, ReplyKeyboardMarkup keyboard);

    /// <summary>
    /// Removes reply keyboard for the user.
    /// </summary>
    Task RemoveReplyKeyboardAsync(long userId, string message);

    /// <summary>
    /// Deletes a message from a chat.
    /// </summary>
    Task DeleteMessageAsync(long chatId, int messageId);

    /// <summary>
    /// Answers a callback query with a toast notification.
    /// </summary>
    Task AnswerCallbackAsync(string callbackId, string messageText);

    /// <summary>
    /// Edits the reply markup (inline keyboard) of an existing message.
    /// </summary>
    Task EditMessageReplyMarkupAsync(long userId, int messageId, InlineKeyboardMarkup keyboard);

    /// <summary>
    /// Edits the text of an existing message.
    /// </summary>
    Task EditMessageReplyTextAsync(long userId, int messageId, string message);

    /// <summary>
    /// Edits both text and reply markup of an existing message in a single API call.
    /// Prefer this over separate EditMessageReplyTextAsync + EditMessageReplyMarkupAsync calls.
    /// </summary>
    Task EditMessageTextWithKeyboardAsync(long userId, int messageId, string message, InlineKeyboardMarkup keyboard);
}
