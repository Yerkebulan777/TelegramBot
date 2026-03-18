using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace TelegramBotServer.Interfaces;

public interface ITelegramOutputService
{
    /// <summary>
    /// Отправляет текстовое сообщение пользователю.
    /// </summary>
    Task<Message?> SendMessageAsync(long userId, string message);

    /// <summary>
    /// Отправляет сообщение об ошибке пользователю.
    /// </summary>
    Task SendErrorAsync(long userId, string errorMessage);

    /// <summary>
    /// Отправляет системное уведомление (например, администратору).
    /// </summary>
    Task SendNotificationAsync(string message);

    /// <summary>
    /// Отправляет сообщение с inline-клавиатурой.
    /// </summary>
    Task<Message?> SendMessageWithKeyboardAsync(long userId, string message, InlineKeyboardMarkup keyboard);

    /// <summary>
    /// Отправляет сообщение с reply-клавиатурой.
    /// </summary>
    Task<Message?> SendMessageWithReplyKeyboardAsync(long userId, string message, ReplyKeyboardMarkup keyboard);

    /// <summary>
    /// Удаляет reply-клавиатуру у пользователя.
    /// </summary>
    Task<Message?> RemoveReplyKeyboardAsync(long userId, string message);

    /// <summary>
    /// Удаляет сообщение из чата.
    /// </summary>
    Task DeleteMessageAsync(long chatId, int messageId);

    /// <summary>
    /// Отвечает на callback-запрос.
    /// </summary>
    Task AnswerCallbackAsync(string callbackId, string messageText);

    /// <summary>
    /// Редактирует inline-клавиатуру сообщения.
    /// </summary>
    Task EditMessageReplyMarkupAsync(long userId, int messageId, InlineKeyboardMarkup keyboard);

    /// <summary>
    /// Редактирует текст сообщения.
    /// </summary>
    Task EditMessageReplyTextAsync(long userId, int messageId, string message);

    /// <summary>
    /// Редактирует текст и клавиатуру сообщения одновременно.
    /// </summary>
    Task EditMessageTextWithKeyboardAsync(long userId, int messageId, string message, InlineKeyboardMarkup keyboard);
}
