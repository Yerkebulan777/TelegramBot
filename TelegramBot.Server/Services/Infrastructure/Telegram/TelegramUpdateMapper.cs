using Telegram.Bot.Types;
using TelegramBot.Core.DTOs;

namespace TelegramBot.Server.Services.Infrastructure.Telegram;

public class TelegramUpdateMapper
{
    private MessageDto MapMessage(Message message)
    {
        var msg = message.From
            ?? throw new InvalidOperationException("Message.From is null.");
        return new MessageDto
        {
            UserId = msg.Id,
            // @handle в приоритете; иначе отображаемое имя. Пустота обоих = аноним (см. CommandAppService).
            Username = msg.Username ?? msg.FirstName,
            ChatId = message.Chat.Id,
            Text = message.Text,
            MessageId = message.MessageId
        };
    }

    private CallbackQueryDto MapCallback(CallbackQuery callback)
    {
        var msg = callback.Message
            ?? throw new InvalidOperationException("CallbackQuery.Message is null.");

        return new CallbackQueryDto
        {
            UserId = callback.From.Id,
            // @handle в приоритете; иначе отображаемое имя. Пустота обоих = аноним (см. CommandAppService).
            Username = callback.From.Username ?? callback.From.FirstName,
            ChatId = msg.Chat.Id,
            MessageText = msg.Text,
            MessageId = msg.MessageId,
            CallbackData = callback.Data,
            CallbackQueryId = callback.Id
        };
    }

    public object? Map(Update update)
    {
        return update.Message?.Text != null && update.Message.From != null
            ? MapMessage(update.Message)
            : update.CallbackQuery != null ? MapCallback(update.CallbackQuery) : null;
    }
}
