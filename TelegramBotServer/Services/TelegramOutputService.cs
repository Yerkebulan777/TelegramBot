using Telegram.Bot;
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

    public async Task<Message?> SendMessageAsync(long userId, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null!;

        try
        {
            var t = await _botClient.SendMessage(
                chatId: new ChatId(userId),
                text: EscapeMarkdownV2(message),
                parseMode: ParseMode.MarkdownV2
            );
            _logger.LogDebug("Sent to {UserId}: {Message}", userId, message);
            return t;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send message to {UserId}", userId);
            return null;
        }
    }

    public async Task SendErrorAsync(long userId, string errorMessage)
    {
        string formatted = $"?? *Error:* {errorMessage}";
        await SendMessageAsync(userId, formatted);
    }

    public async Task SendNotificationAsync(string message)
    {
        if (adminChatId == null)
        {
            _logger.LogInformation("Notification (no admin chat): {Message}", message);
            return;
        }

        await SendMessageAsync(adminChatId.Value, $"?? [Notification]\n{message}");
    }

    public async Task DeleteMessageAsync(long chatId, int messageId)
    {
        try
        {
            await _botClient.DeleteMessage(chatId, messageId);
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException ex)
        {
            if (!ex.Message.Contains("message can't be deleted") && !ex.Message.Contains("message to delete not found"))
                throw;
        }
    }


    public async Task SendMessageWithKeyboardAsync(long userId, string message, InlineKeyboardMarkup keyboard)
    {

        await _botClient.SendMessage(
            chatId: userId,
            text: message,
            replyMarkup: keyboard,
            parseMode: ParseMode.Markdown
        );
    }


    public async Task AnswerCallbackAsync(string callbackId, string messageText)
    {
        await _botClient.AnswerCallbackQuery(callbackQueryId: callbackId, text: messageText);

    }


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
        catch (Telegram.Bot.Exceptions.ApiRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to edit message reply text for {UserId} messageId={MessageId}", userId, messageId);
        }

    }

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
        catch (Telegram.Bot.Exceptions.ApiRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to edit message reply markup for {UserId} messageId={MessageId}", userId, messageId);
        }

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
