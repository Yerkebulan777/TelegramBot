using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Models;
using TelegramBot.Server.Services.Application;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Helpers;

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
        TelegramOutputService outputService,
        MessageTrackingService messageTrackingService,
        long userId,
        UserSession session,
        Func<ReplyKeyboardMarkup> keyboardFactory)
    {
        if (session.LastActionsMessageId.HasValue)
        {
            await outputService.DeleteMessageAsync(userId, session.LastActionsMessageId.Value);
            session.LastActionsMessageId = null;
        }

        var replyKeyboard = keyboardFactory();
        var message = await messageTrackingService.TrackAsync(
            outputService.SendMessageWithReplyKeyboardAsync(userId, "Действия:", replyKeyboard), session);
        if (message != null)
        {
            session.LastActionsMessageId = message.Id;
        }
    }

    /// <summary>
    /// Упрощённая перегрузка, принимающая CallbackContext вместо отдельных параметров.
    /// </summary>
    public static Task SendActionsReplyKeyboardAsync(
        TelegramOutputService outputService,
        MessageTrackingService messageTrackingService,
        CallbackContext context,
        Func<ReplyKeyboardMarkup> keyboardFactory)
    {
        return SendActionsReplyKeyboardAsync(outputService, messageTrackingService, context.UserId, context.Session, keyboardFactory);
    }

    /// <summary>
    /// Отправляет warning/error сообщение с reply-клавиатурой и трекает его.
    /// Используется вместо дублирующихся блоков в FileSelectionHandler, FileNavigationHandler и SlashCommandService.
    /// </summary>
    public static async Task<Message?> SendWarningWithReplyKeyboardAsync(
        TelegramOutputService outputService,
        MessageTrackingService messageTrackingService,
        long userId,
        UserSession session,
        string message,
        Func<ReplyKeyboardMarkup> keyboardFactory)
    {
        var replyKeyboard = keyboardFactory();
        return await messageTrackingService.TrackAsync(
            outputService.SendMessageWithReplyKeyboardAsync(userId, message, replyKeyboard), session);
    }
}
