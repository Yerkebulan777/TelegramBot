using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Models;
using TelegramBot.Data;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Application.Handlers;

/// <summary>
/// Статические вспомогательные методы для callback-хендлеров.
/// Устраняет дублирование SendActionsReplyKeyboardAsync между FileNavigationHandler и CommandSelectionHandler,
/// а также SendFileActionsReplyKeyboardAsync в SlashCommandService.
/// </summary>
internal static class HandlerHelpers
{
    /// <summary>
    /// Удаляет предыдущее сообщение с actions-клавиатурой (если есть), отправляет новое
    /// с переданной reply-клавиатурой и сохраняет messageId в сессии.
    /// </summary>
    public static async Task SendActionsReplyKeyboardAsync(
        ITelegramOutputService outputService,
        MessageTrackingDataService messageTrackingService,
        long userId,
        UserSession session,
        Func<Task<ReplyKeyboardMarkup>> keyboardFactory)
    {
        if (session.LastActionsMessageId.HasValue)
        {
            await outputService.DeleteMessageAsync(userId, session.LastActionsMessageId.Value);
            session.LastActionsMessageId = null;
        }

        var replyKeyboard = await keyboardFactory();
        var message = await outputService.SendMessageWithReplyKeyboardAsync(userId, "Действия:", replyKeyboard);
        if (message != null)
        {
            session.LastActionsMessageId = message.Id;
            var sessionId = session.SessionId > 0 ? session.SessionId : (int?)null;
            await messageTrackingService.TrackMessageAsync(userId, message.Id, sessionId);
        }
    }

    /// <summary>
    /// Упрощённая перегрузка, принимающая CallbackContext вместо отдельных параметров.
    /// </summary>
    public static Task SendActionsReplyKeyboardAsync(
        ITelegramOutputService outputService,
        MessageTrackingDataService messageTrackingService,
        CallbackContext context,
        Func<Task<ReplyKeyboardMarkup>> keyboardFactory)
    {
        return SendActionsReplyKeyboardAsync(outputService, messageTrackingService, context.UserId, context.Session, keyboardFactory);
    }

    /// <summary>
    /// Отправляет warning/error сообщение с reply-клавиатурой и трекает его.
    /// Используется вместо дублирующихся блоков в FileSelectionHandler, FileNavigationHandler и SlashCommandService.
    /// </summary>
    public static async Task<Message?> SendWarningWithReplyKeyboardAsync(
        ITelegramOutputService outputService,
        MessageTrackingDataService messageTrackingService,
        long userId,
        UserSession session,
        string message,
        Func<Task<ReplyKeyboardMarkup>> keyboardFactory)
    {
        var replyKeyboard = await keyboardFactory();
        var sentMessage = await outputService.SendMessageWithReplyKeyboardAsync(userId, message, replyKeyboard);
        if (sentMessage != null)
        {
            var sessionId = session.SessionId > 0 ? session.SessionId : (int?)null;
            await messageTrackingService.TrackMessageAsync(userId, sentMessage.Id, sessionId);
        }
        return sentMessage;
    }
}
