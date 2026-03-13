using Telegram.Bot.Types;

namespace TelegramBotServer.Interfaces
{
    public interface ITelegramUpdateMapper
    {
        //public MessageDto MapMessage(Message message);
        //public CallbackQueryDto MapCallback(CallbackQuery callback);

        Task<object?> Map(Update update);
    }
}
