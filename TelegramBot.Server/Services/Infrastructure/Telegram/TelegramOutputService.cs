using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Models;
using TelegramBot.Server.Helpers;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Infrastructure.Telegram;

public class TelegramOutputService(
    ITelegramBotClient botClient,
    ILogger<TelegramOutputService> logger,
    long? adminChatId = null) : ITelegramOutputService
{
    private const int MaxRetries = 2;

    public async Task<Message?> SendMessageAsync(long userId, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        return await ExecuteWithRetryAsync(async () =>
        {
            var t = await botClient.SendMessage(
                chatId: new ChatId(userId),
                text: MarkdownHelper.EscapeMarkdownV2(message),
                parseMode: ParseMode.MarkdownV2);
            logger.LogDebug("Sent to {UserId}: {Message}", userId, message);
            return t;
        }, userId);
    }

    public async Task<Message?> SendErrorAsync(long userId, string errorMessage)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            var t = await botClient.SendMessage(
                chatId: new ChatId(userId),
                text: $"⚠ Error: {errorMessage}");
            return t;
        }, userId);
    }

    public async Task SendNotificationAsync(string message)
    {
        if (adminChatId == null)
        {
            logger.LogInformation("Notification (no admin chat): {Message}", message);
            return;
        }

        await SendMessageAsync(adminChatId.Value, $"🔔 [Notification]\n{message}");
    }

    public async Task DeleteMessageAsync(long chatId, int messageId)
    {
        try
        {
            await botClient.DeleteMessage(chatId, messageId);
        }
        catch (ApiRequestException ex)
        {
            if (!ex.Message.Contains("message can't be deleted") && !ex.Message.Contains("message to delete not found"))
            {
                throw;
            }
        }
    }

    public async Task DeleteMessagesAsync(long chatId, IEnumerable<int> messageIds, CancellationToken cancellationToken = default)
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
                    "Batch delete failed for {ChatId}; falling back to single deletes for {Count} messages",
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
        await CleanupTrackedMessagesAsync(chatId, session, exceptMessageIds ?? [], CancellationToken.None);
    }

    public async Task CleanupTrackedMessagesAsync(
        long chatId,
        UserSession session,
        IEnumerable<int> keepMessageIds,
        CancellationToken cancellationToken = default)
    {
        var allMessageIds = session.GetTrackedMessages();
        if (allMessageIds.Count == 0)
        {
            return;
        }

        var keepIds = keepMessageIds.ToHashSet();
        var staleIds = allMessageIds
            .Where(messageId => !keepIds.Contains(messageId))
            .ToArray();

        try
        {
            await DeleteMessagesAsync(chatId, staleIds, cancellationToken);
        }
        finally
        {
            session.ClearTrackedMessages();
            foreach (var messageId in allMessageIds.Where(keepIds.Contains))
            {
                session.TrackMessage(messageId);
            }
        }
    }

    public Task<Message?> SendMessageWithReplyKeyboardAsync(long userId, string message, ReplyKeyboardMarkup keyboard)
    {
        return ExecuteWithRetryAsync(() => botClient.SendMessage(
                chatId: userId, text: message, replyMarkup: keyboard, parseMode: ParseMode.Markdown), userId);
    }

    public Task<Message?> RemoveReplyKeyboardAsync(long userId, string message)
    {
        return ExecuteWithRetryAsync(() => botClient.SendMessage(
                chatId: userId, text: message, replyMarkup: new ReplyKeyboardRemove(), parseMode: ParseMode.Markdown), userId);
    }

    public Task<Message?> SendMessageWithKeyboardAsync(long userId, string message, InlineKeyboardMarkup keyboard)
    {
        return ExecuteWithRetryAsync(() => botClient.SendMessage(
                chatId: userId, text: message, replyMarkup: keyboard, parseMode: ParseMode.Markdown), userId);
    }

    public async Task AnswerCallbackAsync(string callbackId, string messageText)
    {
        try
        {
            await botClient.AnswerCallbackQuery(callbackQueryId: callbackId, text: messageText);
        }
        catch (ApiRequestException ex)
        {
            logger.LogWarning(ex, "Failed to answer callback {CallbackId}", callbackId);
        }
    }

    public async Task EditMessageReplyTextAsync(long userId, int messageId, string message)
    {
        try
        {
            await botClient.EditMessageText(chatId: userId, messageId: messageId, text: message);
        }
        catch (ApiRequestException ex) when (IsMessageNotModified(ex))
        {
            logger.LogDebug("Skipped unchanged message reply text edit for {UserId} messageId={MessageId}", userId, messageId);
        }
        catch (ApiRequestException ex)
        {
            logger.LogWarning(ex, "Failed to edit message reply text for {UserId} messageId={MessageId}", userId, messageId);
        }
    }

    public async Task EditMessageReplyMarkupAsync(long userId, int messageId, InlineKeyboardMarkup keyboard)
    {
        try
        {
            await botClient.EditMessageReplyMarkup(chatId: userId, messageId: messageId, replyMarkup: keyboard);
        }
        catch (ApiRequestException ex) when (IsMessageNotModified(ex))
        {
            logger.LogDebug("Skipped unchanged message reply markup edit for {UserId} messageId={MessageId}", userId, messageId);
        }
        catch (ApiRequestException ex)
        {
            logger.LogWarning(ex, "Failed to edit message reply markup for {UserId} messageId={MessageId}", userId, messageId);
        }
    }

    public async Task EditMessageTextWithKeyboardAsync(long userId, int messageId, string message, InlineKeyboardMarkup keyboard)
    {
        try
        {
            await botClient.EditMessageText(chatId: userId, messageId: messageId, text: message, replyMarkup: keyboard);
        }
        catch (ApiRequestException ex) when (IsMessageNotModified(ex))
        {
            logger.LogDebug("Skipped unchanged message text with keyboard edit for {UserId} messageId={MessageId}", userId, messageId);
        }
        catch (ApiRequestException ex)
        {
            logger.LogWarning(ex, "Failed to edit message text with keyboard for {UserId} messageId={MessageId}", userId, messageId);
        }
    }

    private static bool IsMessageNotModified(ApiRequestException ex)
    {
        return ex.ErrorCode == 400
            && ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase);
    }

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
                    "Rate limited for {UserId}. Retrying after {RetryAfterSeconds}s (attempt {Attempt}/{MaxRetries})",
                    userId, retryAfter, attempt + 1, MaxRetries);

                if (attempt < MaxRetries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(retryAfter));
                }
                else
                {
                    logger.LogError(ex, "Rate limit retries exhausted for {UserId}", userId);
                    return null;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send message to {UserId}", userId);
                return null;
            }
        }

        return null;
    }


}
