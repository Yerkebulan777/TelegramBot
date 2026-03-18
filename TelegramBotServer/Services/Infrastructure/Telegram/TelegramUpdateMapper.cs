using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotServer.DTOs;
using TelegramBotServer.Interfaces;

namespace TelegramBotServer.Services
{
    public class TelegramUpdateMapper : ITelegramUpdateMapper
    {
    /// <summary>
    /// Преобразует Telegram Message в DTO.
    /// </summary>
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


    /// <summary>
    /// Преобразует Telegram CallbackQuery в DTO.
    /// </summary>
    public CallbackQueryDto MapCallback(CallbackQuery callback)
        {
            var msg = callback.Message
             ?? throw new InvalidOperationException("CallbackQuery.Message is null.");

            var currentKeyboard = msg.ReplyMarkup as InlineKeyboardMarkup
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


    /// <summary>
    /// Преобразует любое обновление Telegram в DTO (сообщение или callback).
    /// </summary>
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
}
