using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotServer.Interfaces;

namespace TelegramBotServer.Services;

public class TelegramOutputService : ITelegramOutputService
{
    private readonly ITelegramBotClient _botClient;
    private readonly long? _adminChatId;

    public TelegramOutputService(ITelegramBotClient botClient, long? adminChatId = null)
    {
        _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
        _adminChatId = adminChatId;
    }

    public async Task<Message> SendMessageAsync(long userId, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        try
        {
            var t = await _botClient.SendMessage(
                chatId: new ChatId(userId),
                text: EscapeMarkdownV2(message),
                parseMode: ParseMode.MarkdownV2
            );
            Console.WriteLine($"[TelegramOutput] > Sent to {userId}: {message}");
            return t;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TelegramOutput] ? Failed to send message to {userId}: {ex.Message}");
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
        if (_adminChatId == null)
        {
            Console.WriteLine($"[TelegramOutput] Notification: {message}");
            return;
        }

        await SendMessageAsync(_adminChatId.Value, $"?? [Notification]\n{message}");
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
            parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown
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
            Console.WriteLine($"Failed to edit message reply text: {ex.Message}");
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
            Console.WriteLine($"Failed to edit message reply markup: {ex.Message}");
        }

    }
    string EscapeMarkdownV2(string text)
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