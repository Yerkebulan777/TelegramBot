using Telegram.Bot.Types;

namespace TelegramBotServer.Interfaces
{
    public interface ITelegramUpdateMapper
    {
        /// <summary>Преобразует обновление Telegram в DTO.</summary>
        Task<object?> Map(Update update);
    }
}
