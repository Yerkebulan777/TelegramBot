using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Models;
using TelegramBot.Data;
using TelegramBot.Server.Helpers;

namespace TelegramBot.Server.Services.Infrastructure.Telegram;

public class TelegramOutputService(
    ITelegramBotClient botClient,
    MessageTrackingDataService messageTrackingService,
    ILogger<TelegramOutputService> logger)
{
    private const int MaxRetries = 2;

    public async Task<Message?> SendMessageAsync(long userId, string message)
    {
        return string.IsNullOrWhiteSpace(message)
            ? null
            : await ExecuteWithRetryAsync(async () =>
        {
            var t = await botClient.SendMessage(
                chatId: new ChatId(userId),
                text: MarkdownHelper.Escape(message, ParseMode.MarkdownV2),
                parseMode: ParseMode.MarkdownV2);
            logger.LogDebug("Sent: {UserId}: {Message}", userId, message);
            return t;
        }, userId);
    }

    public async Task DeleteMessageAsync(long chatId, int messageId)
    {
        try
        {
            await botClient.DeleteMessage(chatId, messageId);
        }
        catch (ApiRequestException ex)
        {
            if (!ex.Message.Contains("message can't be deleted", StringComparison.OrdinalIgnoreCase)
                && !ex.Message.Contains("message to delete not found", StringComparison.OrdinalIgnoreCase))
            {
                throw;
            }
        }

        // Контракт «удалили в Telegram → удалили из БД»: синхронно вычищаем tracking-строку,
        // чтобы отработанные сообщения не копились как мусор в TrackedMessages.
        // Fire-and-forget по БД (TryExecuteTrackedAsync глушит ошибки) — успех Telegram важнее.
        await messageTrackingService.DeleteTrackedMessagesByChatAsync(chatId, [messageId]);
    }

    private async Task DeleteMessagesAsync(long chatId, IEnumerable<int> messageIds, CancellationToken cancellationToken = default)
    {
        var ids = messageIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        foreach (var chunk in ids.Chunk(100))
        {
            try
            {
                await botClient.DeleteMessages(chatId, chunk, cancellationToken);
            }
            catch (ApiRequestException ex)
            {
                logger.LogWarning(
                    ex,
                    "Batch delete fail: {ChatId}, fallback single for {Count}",
                    chatId,
                    chunk.Length);

                foreach (var messageId in chunk)
                {
                    await DeleteMessageAsync(chatId, messageId);
                }
            }
        }
    }

    public async Task ClearChatHistoryAsync(long chatId, UserSession session, IEnumerable<int>? exceptMessageIds = null)
    {
        // Delete the user's own last message (slash command or reply keyboard button press)
        if (session.LastUserMessageId.HasValue)
        {
            await DeleteMessageAsync(chatId, session.LastUserMessageId.Value);
            session.LastUserMessageId = null;
        }

        await CleanupTrackedMessagesInternalAsync(chatId, session, exceptMessageIds ?? [], CancellationToken.None);
    }

    private async Task CleanupTrackedMessagesInternalAsync(
        long chatId,
        UserSession session,
        IEnumerable<int> keepMessageIds,
        CancellationToken cancellationToken)
    {
        var trackedMessageIds = await messageTrackingService.GetTrackedMessagesByChatAsync(chatId);
        if (trackedMessageIds.Count == 0)
        {
            return;
        }

        var keepIds = keepMessageIds.ToHashSet();
        var staleIds = trackedMessageIds
            .Where(messageId => !keepIds.Contains(messageId))
            .ToArray();

        if (staleIds.Length == 0)
        {
            return;
        }

        await DeleteMessagesAsync(chatId, staleIds, cancellationToken);
        await messageTrackingService.DeleteTrackedMessagesByChatAsync(chatId, staleIds);
    }

    public async Task<Message?> SendMessageWithReplyKeyboardAsync(long userId, string message, ReplyKeyboardMarkup keyboard)
    {
        return await ExecuteWithRetryAsync(() => botClient.SendMessage(
                chatId: userId, text: message, replyMarkup: keyboard, parseMode: ParseMode.Markdown), userId);
    }

    public async Task<Message?> RemoveReplyKeyboardAsync(long userId, string message)
    {
        return await ExecuteWithRetryAsync(() => botClient.SendMessage(
                chatId: userId, text: message, replyMarkup: new ReplyKeyboardRemove(), parseMode: ParseMode.Markdown), userId);
    }

    public async Task<Message?> SendMessageWithKeyboardAsync(long userId, string message, InlineKeyboardMarkup keyboard)
    {
        // Сначала пробуем отправить с inline-клавиатурой без retry-цикла, чтобы
        // поймать permanent-ошибку «reply markup is too long» и сразу упасть в
        // fallback на plain-text (без клавиатуры). Для остальных transient-ошибок
        // (429, сетевые) — делегируем в ExecuteWithRetryAsync.
        try
        {
            return await botClient.SendMessage(
                chatId: userId, text: message, replyMarkup: keyboard, parseMode: ParseMode.Markdown);
        }
        catch (ApiRequestException ex) when (IsReplyMarkupTooLong(ex))
        {
            logger.LogWarning(
                "Reply markup too long: {UserId}, fallback text-only",
                userId);
            return await ExecuteWithRetryAsync(() => botClient.SendMessage(
                chatId: userId, text: message, parseMode: ParseMode.Markdown), userId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Transient-ошибка — попадаем в retry-цикл (429, сетевые и т.п.).
            return await ExecuteWithRetryAsync(() => botClient.SendMessage(
                chatId: userId, text: message, replyMarkup: keyboard, parseMode: ParseMode.Markdown), userId);
        }
    }

    public async Task AnswerCallbackAsync(string callbackId, string messageText)
    {
        try
        {
            await botClient.AnswerCallbackQuery(callbackQueryId: callbackId, text: messageText);
        }
        catch (ApiRequestException ex)
        {
            logger.LogWarning(ex, "Answer callback fail: {CallbackId}", callbackId);
        }
    }

    public async Task EditMessageReplyTextAsync(long userId, int messageId, string message)
    {
        try
        {
            _=await botClient.EditMessageText(chatId: userId, messageId: messageId, text: message);
        }
        catch (ApiRequestException ex) when (IsMessageNotModified(ex))
        {
            logger.LogDebug("Reply text unchanged: {UserId} msg={MessageId}", userId, messageId);
        }
        catch (ApiRequestException ex)
        {
            logger.LogWarning(ex, "Edit reply text fail: {UserId} msg={MessageId}", userId, messageId);
        }
    }

    public async Task EditMessageReplyMarkupAsync(long userId, int messageId, InlineKeyboardMarkup keyboard)
    {
        try
        {
            _=await botClient.EditMessageReplyMarkup(chatId: userId, messageId: messageId, replyMarkup: keyboard);
        }
        catch (ApiRequestException ex) when (IsMessageNotModified(ex))
        {
            logger.LogDebug("Reply markup unchanged: {UserId} msg={MessageId}", userId, messageId);
        }
        catch (ApiRequestException ex) when (IsReplyMarkupTooLong(ex))
        {
            // Существующая клавиатура остаётся — фронт не ломаем, только логируем.
            logger.LogWarning(
                "Reply markup too long: {UserId} msg={MessageId}; keyboard kept",
                userId, messageId);
        }
        catch (ApiRequestException ex)
        {
            logger.LogWarning(ex, "Edit reply markup fail: {UserId} msg={MessageId}", userId, messageId);
        }
    }

    public async Task EditMessageTextWithKeyboardAsync(long userId, int messageId, string message, InlineKeyboardMarkup keyboard)
    {
        try
        {
            _=await botClient.EditMessageText(chatId: userId, messageId: messageId, text: message, replyMarkup: keyboard);
        }
        catch (ApiRequestException ex) when (IsMessageNotModified(ex))
        {
            logger.LogDebug("Text+keyboard unchanged: {UserId} msg={MessageId}", userId, messageId);
        }
        catch (ApiRequestException ex) when (IsReplyMarkupTooLong(ex))
        {
            logger.LogWarning(
                "Reply markup too long: {UserId} msg={MessageId}; fallback text-only",
                userId, messageId);
            try
            {
                _=await botClient.EditMessageText(chatId: userId, messageId: messageId, text: message);
            }
            catch (ApiRequestException fbEx) when (IsMessageNotModified(fbEx))
            {
                logger.LogDebug(
                    "Text fallback unchanged: {UserId} msg={MessageId}", userId, messageId);
            }
            catch (ApiRequestException fbEx)
            {
                logger.LogWarning(
                    fbEx,
                    "Text fallback edit fail: {UserId} msg={MessageId}", userId, messageId);
            }
        }
        catch (ApiRequestException ex)
        {
            logger.LogWarning(ex, "Edit text+keyboard fail: {UserId} msg={MessageId}", userId, messageId);
        }
    }

    public async Task SendChatActionAsync(long userId, CancellationToken cancellationToken = default)
    {
        try
        {
            await botClient.SendChatAction(chatId: userId, action: ChatAction.Typing, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ApiRequestException ex)
        {
            logger.LogDebug(ex, "Send chat action fail: {UserId}", userId);
        }
    }

    private static bool IsMessageNotModified(ApiRequestException ex)
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
    private static bool IsReplyMarkupTooLong(ApiRequestException ex)
    {
        return ex.ErrorCode == 400
            && ex.Message.Contains("reply markup", StringComparison.OrdinalIgnoreCase)
            && ex.Message.Contains("too long", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Retry-цикл: для rate limit (429) ждёт RetryAfter, для остальных ошибок — exponential backoff.
    /// После исчерпания попыток возвращает null, не прерывая поток.
    /// </summary>
    private async Task<Message?> ExecuteWithRetryAsync(Func<Task<Message>> action, long userId)
    {
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return await action();
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 429)
            {
                var retryAfter = ex.Parameters?.RetryAfter ?? 5;
                logger.LogWarning(
                    "Rate limit: {UserId}, retry {RetryAfterSeconds}s (attempt {Attempt}/{MaxRetries})",
                    userId, retryAfter, attempt + 1, MaxRetries);

                if (attempt < MaxRetries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(retryAfter));
                }
                else
                {
                    logger.LogError(ex, "Rate limit retries exhausted: {UserId}", userId);
                    return null;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Send fail: {UserId} (attempt {Attempt}/{MaxRetries})",
                    userId, attempt + 1, MaxRetries);

                if (attempt < MaxRetries)
                {
                    // Exponential backoff for non-rate-limit errors
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
                }
                else
                {
                    logger.LogError(ex, "Send retries exhausted: {UserId}", userId);
                    return null;
                }
            }
        }

        return null;
    }


}
