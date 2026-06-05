using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;
using TelegramBot.Server.Helpers;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Infrastructure.Telegram;

public class TelegramOutputService(
    ITelegramBotClient botClient,
    IDataService dataService,
    ILogger<TelegramOutputService> logger,
    long? adminChatId = null) : ITelegramOutputService
{
    private readonly ITelegramBotClient _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
    private readonly IDataService _dataService = dataService;
    private readonly ILogger<TelegramOutputService> _logger = logger;
    private const int MaxRetries = 2;

    public async Task<Message?> SendMessageAsync(long userId, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        
        var msg = await ExecuteWithRetryAsync(async () =>
        {
            var t = await _botClient.SendMessage(
                chatId: new ChatId(userId),
                text: MarkdownHelper.EscapeMarkdownV2(message),
                parseMode: ParseMode.MarkdownV2);
            _logger.LogDebug("Sent to {UserId}: {Message}", userId, message);
            return t;
        }, userId);

        if (msg != null)
        {
            _ = Task.Run(() => _dataService.SaveTrackedMessageAsync(userId, msg.Id));
        }

        return msg;
    }

    public async Task<Message?> SendErrorAsync(long userId, string errorMessage)
    {
        var msg = await ExecuteWithRetryAsync(async () =>
        {
            var t = await _botClient.SendMessage(
                chatId: new ChatId(userId),
                text: $"⚠ Error: {errorMessage}");
            return t;
        }, userId);

        if (msg != null)
        {
            _ = Task.Run(() => _dataService.SaveTrackedMessageAsync(userId, msg.Id));
        }

        return msg;
    }

    public async Task SendNotificationAsync(string message)
    {
        if (adminChatId == null)
        {
            _logger.LogInformation("Notification (no admin chat): {Message}", message);
            return;
        }

        _=await SendMessageAsync(adminChatId.Value, $"🔔 [Notification]\n{message}");
    }

    public async Task DeleteMessageAsync(long chatId, int messageId)
    {
        try
        {
            await _botClient.DeleteMessage(chatId, messageId);
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
                await _botClient.DeleteMessages(chatId, chunk, cancellationToken);
            }
            catch (ApiRequestException ex)
            {
                _logger.LogWarning(
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

    public async Task ClearChatHistoryAsync(long chatId, UserSession session)
    {
        var messageIds = session.GetTrackedMessages();
        if (messageIds.Count == 0)
        {
            return;
        }

        try
        {
            await DeleteMessagesAsync(chatId, messageIds);
        }
        finally
        {
            session.ClearTrackedMessages();
            _ = Task.Run(async () => await _dataService.DeleteTrackedMessagesAsync(chatId));
        }
    }

    public Task<Message?> SendMessageWithReplyKeyboardAsync(long userId, string message, ReplyKeyboardMarkup keyboard)
    {
        return TrackAsync(ExecuteWithRetryAsync(() => _botClient.SendMessage(
                chatId: userId, text: message, replyMarkup: keyboard, parseMode: ParseMode.Markdown), userId), userId);
    }

    public Task<Message?> RemoveReplyKeyboardAsync(long userId, string message)
    {
        return TrackAsync(ExecuteWithRetryAsync(() => _botClient.SendMessage(
                chatId: userId, text: message, replyMarkup: new ReplyKeyboardRemove(), parseMode: ParseMode.Markdown), userId), userId);
    }

    public Task<Message?> SendMessageWithKeyboardAsync(long userId, string message, InlineKeyboardMarkup keyboard)
    {
        return TrackAsync(ExecuteWithRetryAsync(() => _botClient.SendMessage(
                chatId: userId, text: message, replyMarkup: keyboard, parseMode: ParseMode.Markdown), userId), userId);
    }

    private async Task<Message?> TrackAsync(Task<Message?> task, long userId)
    {
        var msg = await task;
        if (msg != null)
        {
            _ = Task.Run(() => _dataService.SaveTrackedMessageAsync(userId, msg.Id));
        }
        return msg;
    }

    public async Task AnswerCallbackAsync(string callbackId, string messageText)
    {
        try
        {
            await _botClient.AnswerCallbackQuery(callbackQueryId: callbackId, text: messageText);
        }
        catch (ApiRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to answer callback {CallbackId}", callbackId);
        }
    }

    public async Task EditMessageReplyTextAsync(long userId, int messageId, string message)
    {
        try
        {
            _=await _botClient.EditMessageText(chatId: userId, messageId: messageId, text: message);
        }
        catch (ApiRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to edit message reply text for {UserId} messageId={MessageId}", userId, messageId);
        }
    }

    public async Task EditMessageReplyMarkupAsync(long userId, int messageId, InlineKeyboardMarkup keyboard)
    {
        try
        {
            _=await _botClient.EditMessageReplyMarkup(chatId: userId, messageId: messageId, replyMarkup: keyboard);
        }
        catch (ApiRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to edit message reply markup for {UserId} messageId={MessageId}", userId, messageId);
        }
    }

    public async Task EditMessageTextWithKeyboardAsync(long userId, int messageId, string message, InlineKeyboardMarkup keyboard)
    {
        try
        {
            _=await _botClient.EditMessageText(chatId: userId, messageId: messageId, text: message, replyMarkup: keyboard);
        }
        catch (ApiRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to edit message text with keyboard for {UserId} messageId={MessageId}", userId, messageId);
        }
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
                _logger.LogWarning(
                    "Rate limited for {UserId}. Retrying after {RetryAfterSeconds}s (attempt {Attempt}/{MaxRetries})",
                    userId, retryAfter, attempt + 1, MaxRetries);

                if (attempt < MaxRetries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(retryAfter));
                }
                else
                {
                    _logger.LogError(ex, "Rate limit retries exhausted for {UserId}", userId);
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send message to {UserId}", userId);
                return null;
            }
        }

        return null;
    }


}
