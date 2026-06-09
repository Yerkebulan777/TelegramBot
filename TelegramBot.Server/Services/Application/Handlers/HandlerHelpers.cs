using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;
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
        IDataService dataService,
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
            await dataService.TrackMessageAsync(userId, message.Id, sessionId);
        }
    }

    /// <inheritdoc cref="SendActionsReplyKeyboardAsync(ITelegramOutputService, IDataService, long, UserSession, Func{Task{ReplyKeyboardMarkup}})"/>
    public static Task SendActionsReplyKeyboardAsync(
        ITelegramOutputService outputService,
        IDataService dataService,
        CallbackContext context,
        Func<Task<ReplyKeyboardMarkup>> keyboardFactory)
    {
        return SendActionsReplyKeyboardAsync(outputService, dataService, context.UserId, context.Session, keyboardFactory);
    }
}
