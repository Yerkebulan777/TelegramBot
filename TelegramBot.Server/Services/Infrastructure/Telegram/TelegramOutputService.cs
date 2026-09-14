using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Data;
using TelegramBot.Data.Models;
using TelegramBot.Server.Helpers;

namespace TelegramBot.Server.Services.Infrastructure.Telegram;

public class TelegramOutputService(
    ITelegramBotClient botClient,
    MessageTrackingDataService messageTrackingService,
    ILogger<TelegramOutputService> logger)
{
    private const int MaxRetries = 2;

    /// <summary>One plain-text send attempt; the durable notification sender owns retries.</summary>
    public Task<Message> SendNotificationAsync(long userId, string message, CancellationToken cancellationToken) =>
        botClient.SendMessage(chatId: userId, text: message, cancellationToken: cancellationToken);

    public async Task<Message?> SendMessageAsync(long userId, string message, CancellationToken cancellationToken = default)
    {
        return string.IsNullOrWhiteSpace(message)
            ? null
            : await ExecuteWithRetryAsync(async () =>
        {
            var t = await botClient.SendMessage(
                chatId: new ChatId(userId),
                text: MarkdownHelper.Escape(message, ParseMode.MarkdownV2),
                parseMode: ParseMode.MarkdownV2,
                cancellationToken: cancellationToken);
            logger.LogDebug("Sent: {UserId}: {Message}", userId, message);
            return t;
        }, userId, cancellationToken);
    }

    private async Task<bool> TryDeleteMessageAsync(long chatId, int messageId, CancellationToken cancellationToken)
    {
        try
        {
            await botClient.DeleteMessage(chatId, messageId, cancellationToken);
            return true;
        }
        catch (ApiRequestException ex) when (IsMessageToDeleteMissing(ex))
        {
            return true;
        }
        catch (ApiRequestException ex) when (IsMessageDeletionRefused(ex))
        {
            logger.LogWarning(
                "Telegram refused deletion: chat={ChatId}, message={MessageId}, error={Error}",
                chatId,
                messageId,
                ex.Message);
            return false;
        }
    }

    private async Task<HashSet<int>> DeleteMessagesAsync(
        long chatId,
        IEnumerable<int> messageIds,
        CancellationToken cancellationToken)
    {
        var deletedIds = new HashSet<int>();
        foreach (var chunk in messageIds.Distinct().Chunk(100))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await DeleteBatchAsync(chatId, chunk, cancellationToken);
            // Telegram may have deleted part of a batch before shutdown; persist that progress
            // with an independent, bounded budget before propagating cancellation.
            using var progressCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await messageTrackingService.SaveDeletionProgressAsync(
                chatId, result.DeletedIds, chunk.Except(result.DeletedIds), result.NextAttemptAt, progressCts.Token);
            deletedIds.UnionWith(result.DeletedIds);
            // Persist partial progress before propagating cancellation.
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Deferred)
            {
                break;
            }
        }
        return deletedIds;
    }

    private sealed record DeletionBatchResult(IReadOnlyList<int> DeletedIds, bool Deferred, DateTime NextAttemptAt);

    private async Task<DeletionBatchResult> DeleteBatchAsync(long chatId, int[] messageIds, CancellationToken cancellationToken)
    {
        var deletedIds = new List<int>(messageIds.Length);
        try
        {
            await botClient.DeleteMessages(chatId, messageIds, cancellationToken);
            return new(messageIds, false, DateTime.UtcNow);
        }
        catch (ApiRequestException ex) when (IsMessageDeletionRefused(ex) || IsMessageToDeleteMissing(ex))
        {
            // Only message-specific errors justify trying individual messages.
        }
        catch (Exception ex) when (ex is ApiRequestException or HttpRequestException or OperationCanceledException)
        {
            LogDeferredDeletion(ex, chatId, messageIds.Length, cancellationToken);
            return new(deletedIds, true, GetNextDeletionAttemptAt(ex));
        }

        try
        {
            foreach (var messageId in messageIds)
            {
                if (await TryDeleteMessageAsync(chatId, messageId, cancellationToken))
                {
                    deletedIds.Add(messageId);
                }
            }
            return new(deletedIds, false, DateTime.UtcNow.AddMinutes(1));
        }
        catch (Exception ex) when (ex is ApiRequestException or HttpRequestException or OperationCanceledException)
        {
            LogDeferredDeletion(ex, chatId, messageIds.Length - deletedIds.Count, cancellationToken);
            return new(deletedIds, true, GetNextDeletionAttemptAt(ex));
        }
    }

    private void LogDeferredDeletion(Exception exception, long chatId, int count, CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Message deletion deferred: chat={ChatId}, count={Count}", chatId, count);
        }
    }

    private static DateTime GetNextDeletionAttemptAt(Exception exception) =>
        DateTime.UtcNow.AddSeconds(exception is ApiRequestException { ErrorCode: 429 } request
            ? Math.Max(60, request.Parameters?.RetryAfter ?? 60)
            : 60);

    public async Task DeleteTemporaryMessagesAsync(long chatId, CancellationToken cancellationToken)
    {
        var temporaryIds = await messageTrackingService.GetTrackedMessagesByKindAsync(
            chatId, TrackedMessageKinds.Temporary, cancellationToken);
        if (temporaryIds.Count == 0)
        {
            return;
        }

        var dueIds = await messageTrackingService.ScheduleDeletionByChatAsync(
            chatId, temporaryIds, cancellationToken);
        _ = await DeleteMessagesAsync(chatId, dueIds, cancellationToken);
    }

    public async Task ClearChatHistoryAsync(
        long chatId, UserSession session, CancellationToken cancellationToken, IEnumerable<int>? exceptMessageIds = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var trackedMessageIds = await messageTrackingService.GetTrackedMessagesByChatAsync(chatId, cancellationToken);
        var keepIds = (exceptMessageIds ?? []).ToHashSet();
        var lastMessageId = session.LastUserMessageId;
        var candidates = lastMessageId is { } id
            ? trackedMessageIds.Append(id)
            : trackedMessageIds;
        var staleIds = candidates
            .Where(messageId => !keepIds.Contains(messageId))
            .ToArray();

        if (staleIds.Length == 0)
        {
            return;
        }

        var dueIds = await messageTrackingService.ScheduleDeletionByChatAsync(chatId, staleIds, cancellationToken);
        var deletedIds = await DeleteMessagesAsync(chatId, dueIds, cancellationToken);
        if (lastMessageId is { } lastId && deletedIds.Contains(lastId))
        {
            session.LastUserMessageId = null;
        }
    }

    /// <summary>
    /// Удаляет заданные устаревшие сообщения и освобождает только успешно удалённые tracking-записи.
    /// </summary>
    public async Task<int> CleanupTrackedMessagesAsync(
        IEnumerable<TrackedMessageReference> trackedMessages,
        CancellationToken cancellationToken)
    {
        var deletedCount = 0;
        foreach (var chatMessages in trackedMessages.GroupBy(message => message.ChatId))
        {
            var deletedIds = await DeleteMessagesAsync(
                chatMessages.Key,
                chatMessages.Select(message => message.MessageId),
                cancellationToken);
            deletedCount += deletedIds.Count;
        }
        return deletedCount;
    }

    public async Task<Message?> RemoveReplyKeyboardAsync(
        long userId, string message, CancellationToken cancellationToken = default)
    {
        return await ExecuteWithRetryAsync(
            () => botClient.SendMessage(
                chatId: userId, text: message, replyMarkup: new ReplyKeyboardRemove(), parseMode: ParseMode.Markdown, cancellationToken: cancellationToken),
            userId,
            cancellationToken);
    }

    public async Task<Message?> SendMessageWithKeyboardAsync(
        long userId, string message, InlineKeyboardMarkup keyboard, CancellationToken cancellationToken = default)
    {
        // Сначала пробуем отправить с inline-клавиатурой без retry-цикла, чтобы
        // поймать permanent-ошибку «reply markup is too long» и сразу упасть в
        // fallback на plain-text (без клавиатуры). Для остальных transient-ошибок
        // (429, сетевые) — делегируем в ExecuteWithRetryAsync.
        try
        {
            return await botClient.SendMessage(
                chatId: userId, text: message, replyMarkup: keyboard, parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
        }
        catch (ApiRequestException ex) when (IsReplyMarkupTooLong(ex))
        {
            logger.LogWarning(
                "Reply markup too long: {UserId}, fallback text-only",
                userId);
            return await ExecuteWithRetryAsync(
                () => botClient.SendMessage(
                    chatId: userId, text: message, parseMode: ParseMode.Markdown, cancellationToken: cancellationToken),
                userId,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Transient-ошибка — попадаем в retry-цикл (429, сетевые и т.п.).
            return await ExecuteWithRetryAsync(
                () => botClient.SendMessage(
                    chatId: userId, text: message, replyMarkup: keyboard, parseMode: ParseMode.Markdown, cancellationToken: cancellationToken),
                userId,
                cancellationToken);
        }
    }

    public Task<Message?> SendForceReplyAsync(long userId, string message, CancellationToken cancellationToken = default)
    {
        return ExecuteWithRetryAsync(
            () => botClient.SendMessage(
                chatId: userId,
                text: message,
                replyMarkup: new ForceReplyMarkup(),
                parseMode: ParseMode.Markdown,
                cancellationToken: cancellationToken),
            userId,
            cancellationToken);
    }

    public async Task AnswerCallbackAsync(CallbackContext context, string messageText)
    {
        if (context.CallbackAnswered)
        {
            return;
        }

        await AnswerCallbackAsync(context.CallbackQueryId, messageText);
        context.CallbackAnswered = true;
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

    /// <summary>
    /// Retry-цикл: для rate limit (429) ждёт RetryAfter, для остальных ошибок — exponential backoff.
    /// После исчерпания попыток возвращает null, не прерывая поток.
    /// </summary>
    private async Task<Message?> ExecuteWithRetryAsync(
        Func<Task<Message>> action, long userId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await action();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 429)
            {
                var retryAfter = ex.Parameters?.RetryAfter ?? 5;
                logger.LogWarning(
                    "Rate limit: {UserId}, retry {RetryAfterSeconds}s (attempt {Attempt}/{MaxRetries})",
                    userId, retryAfter, attempt + 1, MaxRetries);

                if (attempt < MaxRetries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(retryAfter), cancellationToken);
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
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
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

    private static bool IsMessageToDeleteMissing(ApiRequestException ex) =>
        ex.ErrorCode == 400 && ex.Message.Contains("message to delete not found", StringComparison.OrdinalIgnoreCase);

    private static bool IsMessageDeletionRefused(ApiRequestException ex) =>
        ex.ErrorCode == 400 && ex.Message.Contains("message can't be deleted", StringComparison.OrdinalIgnoreCase);

    private static bool IsMessageNotModified(ApiRequestException ex) =>
        ex.ErrorCode == 400
        && ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase);

    private static bool IsReplyMarkupTooLong(ApiRequestException ex) =>
        ex.ErrorCode == 400
        && ex.Message.Contains("reply markup", StringComparison.OrdinalIgnoreCase)
        && ex.Message.Contains("too long", StringComparison.OrdinalIgnoreCase);
}
