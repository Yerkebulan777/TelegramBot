using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotServer.Interfaces;

namespace TelegramBotServer.Services;

public class TelegramOutputService(
    ITelegramBotClient botClient,
    ILogger<TelegramOutputService> logger,
    long? adminChatId = null) : ITelegramOutputService
{
    private readonly ITelegramBotClient _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
    private readonly ILogger<TelegramOutputService> _logger = logger;
    private const int MaxRetries = 2;

    /// <summary>
    /// Отправляет текстовое сообщение пользователю с MarkdownV2 форматированием.
    /// </summary>
    public async Task<Message?> SendMessageAsync(long userId, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null!;

        return await ExecuteWithRetryAsync(async () =>
        {
            var t = await _botClient.SendMessage(
                chatId: new ChatId(userId),
                text: EscapeMarkdownV2(message),
                parseMode: ParseMode.MarkdownV2
            );
            _logger.LogDebug("Sent to {UserId}: {Message}", userId, message);
            return t;
        }, userId);
    }

    /// <summary>
    /// Отправляет сообщение об ошибке пользователю (обычный текст).
    /// </summary>
    public async Task SendErrorAsync(long userId, string errorMessage)
    {
        // Send as plain text to avoid MarkdownV2 escaping issues with formatting
        await ExecuteWithRetryAsync(async () =>
        {
            var t = await _botClient.SendMessage(
                chatId: new ChatId(userId),
                text: $"⚠ Error: {errorMessage}"
            );
            return t;
        }, userId);
    }

    /// <summary>
    /// Отправляет уведомление администратору бота.
    /// </summary>
    public async Task SendNotificationAsync(string message)
    {
        if (adminChatId == null)
        {
            _logger.LogInformation("Notification (no admin chat): {Message}", message);
            return;
        }

        await SendMessageAsync(adminChatId.Value, $"🔔 [Notification]\n{message}");
    }

    /// <summary>
    /// Удаляет сообщение по ID.
    /// </summary>
    public async Task DeleteMessageAsync(long chatId, int messageId)
    {
        try
        {
            await _botClient.DeleteMessage(chatId, messageId);
        }
        catch (ApiRequestException ex)
        {
            if (!ex.Message.Contains("message can't be deleted") && !ex.Message.Contains("message to delete not found"))
                throw;
        }
    }

    /// <summary>
    /// Отправляет сообщение с reply-клавиатурой.
    /// </summary>
    public async Task<Message?> SendMessageWithReplyKeyboardAsync(long userId, string message, ReplyKeyboardMarkup keyboard)
    {
        try
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                var t = await _botClient.SendMessage(
                    chatId: userId,
                    text: message,
                    replyMarkup: keyboard,
                    parseMode: ParseMode.Markdown
                );
                return t;
            }, userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send message with reply keyboard to {UserId}", userId);
            return null;
        }
    }

    /// <summary>
    /// Отправляет сообщение с удалением reply-клавиатуры.
    /// </summary>
    public async Task<Message?> RemoveReplyKeyboardAsync(long userId, string message)
    {
        try
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                var t = await _botClient.SendMessage(
                    chatId: userId,
                    text: message,
                    replyMarkup: new ReplyKeyboardRemove(),
                    parseMode: ParseMode.Markdown
                );
                return t;
            }, userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove reply keyboard for {UserId}", userId);
            return null;
        }
    }


    /// <summary>
    /// Отправляет сообщение с inline-клавиатурой.
    /// </summary>
    public async Task<Message?> SendMessageWithKeyboardAsync(long userId, string message, InlineKeyboardMarkup keyboard)
    {
        try
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                var t = await _botClient.SendMessage(
                    chatId: userId,
                    text: message,
                    replyMarkup: keyboard,
                    parseMode: ParseMode.Markdown
                );
                return t;
            }, userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send message with keyboard to {UserId}", userId);
            return null;
        }
    }


    /// <summary>
    /// Отвечает на callback-запрос (закрывает всплывающее уведомление).
    /// </summary>
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


    /// <summary>
    /// Редактирует текст сообщения (без клавиатуры).
    /// </summary>
    public async Task EditMessageReplyTextAsync(long userId, int messageId, string message)
    {
        try
        {
            await _botClient.EditMessageText(
                chatId: userId,
                messageId: messageId,
                text: message
            );
        }
        catch (ApiRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to edit message reply text for {UserId} messageId={MessageId}", userId, messageId);
        }
    }

    /// <summary>
    /// Редактирует клавиатуру сообщения (без текста).
    /// </summary>
    public async Task EditMessageReplyMarkupAsync(long userId, int messageId, InlineKeyboardMarkup keyboard)
    {
        try
        {
            await _botClient.EditMessageReplyMarkup(
                chatId: userId,
                messageId: messageId,
                replyMarkup: keyboard
            );
        }
        catch (ApiRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to edit message reply markup for {UserId} messageId={MessageId}", userId, messageId);
        }
    }

    /// <summary>
    /// Редактирует текст сообщения и клавиатуру вместе.
    /// </summary>
    public async Task EditMessageTextWithKeyboardAsync(long userId, int messageId, string message, InlineKeyboardMarkup keyboard)
    {
        try
        {
            await _botClient.EditMessageText(
                chatId: userId,
                messageId: messageId,
                text: message,
                replyMarkup: keyboard
            );
        }
        catch (ApiRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to edit message text with keyboard for {UserId} messageId={MessageId}", userId, messageId);
        }
    }

    /// <summary>
    /// Executes a Telegram API call with retry logic for 429 (Too Many Requests) errors.
    /// </summary>
    private async Task<Message?> ExecuteWithRetryAsync(Func<Task<Message>> action, long userId)
    {
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
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

    static string EscapeMarkdownV2(string text)
    {
        return text
            .Replace("\\", "\\\\")
            .Replace("_", "\\_")
            .Replace("*", "\\*")
            .Replace("[", "\\[")
            .Replace("]", "\\]")
            .Replace("(", "\\(")
            .Replace(")", "\\)")
            .Replace("~", "\\~")
            .Replace("`", "\\`")
            .Replace(">", "\\>")
            .Replace("#", "\\#")
            .Replace("+", "\\+")
            .Replace("-", "\\-")
            .Replace("=", "\\=")
            .Replace("|", "\\|")
            .Replace("{", "\\{")
            .Replace("}", "\\}")
            .Replace(".", "\\.")
            .Replace("!", "\\!");
    }
}
