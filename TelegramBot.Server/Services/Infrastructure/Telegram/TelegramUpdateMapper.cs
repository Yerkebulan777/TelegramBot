using Telegram.Bot.Types;
using TelegramBot.Core.DTOs;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Infrastructure.Telegram;

public class TelegramUpdateMapper : ITelegramUpdateMapper
{
    public MessageDto MapMessage(Message message)
    {
        var msg = message.From
            ?? throw new InvalidOperationException("Message.From is null.");
        return new MessageDto
        {
            UserId = msg.Id,
            Username = msg.Username,
            ChatId = message.Chat.Id,
            Text = message.Text,
            Date = message.Date,
            MessageId = message.MessageId
        };
    }

    public CallbackQueryDto MapCallback(CallbackQuery callback)
    {
        var msg = callback.Message
            ?? throw new InvalidOperationException("CallbackQuery.Message is null.");

        var currentKeyboard = msg.ReplyMarkup
            ?? throw new InvalidOperationException("CallbackQuery.Message.ReplyMarkup is null.");

        return new CallbackQueryDto
        {
            UserId = callback.From.Id,
            Username = callback.From.Username,
            ChatId = msg.Chat.Id,
            MessageText = msg.Text,
            MessageId = msg.MessageId,
            CallbackData = callback.Data,
            CallbackQueryId = callback.Id,
            Buttons = currentKeyboard.InlineKeyboard.Select(row => row
                .Select(btn => new ButtonDto
                {
                    Text = btn.Text,
                    CallbackData = btn.CallbackData
                }).ToList())
                .ToList()
        };
    }

    public Task<object?> Map(Update update)
    {
        if (update.Message?.Text != null && update.Message.From != null)
        {
            return Task.FromResult<object?>(MapMessage(update.Message));
        }
        else if (update.CallbackQuery != null)
        {
            return Task.FromResult<object?>(MapCallback(update.CallbackQuery));
        }

        return Task.FromResult<object?>(null);
    }
}
