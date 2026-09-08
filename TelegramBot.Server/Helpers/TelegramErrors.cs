using Telegram.Bot.Exceptions;

namespace TelegramBot.Server.Helpers;

/// <summary>
/// Классификация ответов Telegram Bot API по <see cref="ApiRequestException"/>.
/// Выделено из <c>TelegramOutputService</c>: чистые предикаты над исключением,
/// не зависят от состояния сервиса и переиспользуются всеми методами отправки/редактирования.
/// </summary>
public static class TelegramErrors
{
    public static bool IsMessageToDeleteMissing(ApiRequestException ex) =>
        ex.ErrorCode == 400 && ex.Message.Contains("message to delete not found", StringComparison.OrdinalIgnoreCase);

    public static bool IsMessageDeletionRefused(ApiRequestException ex) =>
        ex.ErrorCode == 400 && ex.Message.Contains("message can't be deleted", StringComparison.OrdinalIgnoreCase);

    public static bool IsMessageNotModified(ApiRequestException ex)
    {
        return ex.ErrorCode == 400
            && ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Telegram API: HTTP 400 «Bad Request: reply markup is too long».
    /// Срабатывает, когда суммарный размер callback_data всех кнопок inline-клавиатуры
    /// превышает ~4096 байт. Лечится пагинацией (см. KeyboardBuilder.SessionsPageSize)
    /// или fallback на текст без клавиатуры.
    /// </summary>
    public static bool IsReplyMarkupTooLong(ApiRequestException ex)
    {
        return ex.ErrorCode == 400
            && ex.Message.Contains("reply markup", StringComparison.OrdinalIgnoreCase)
            && ex.Message.Contains("too long", StringComparison.OrdinalIgnoreCase);
    }
}
