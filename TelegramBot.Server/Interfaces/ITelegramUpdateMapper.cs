using Telegram.Bot.Types;

namespace TelegramBot.Server.Interfaces;

public interface ITelegramUpdateMapper
{
    Task<object?> Map(Update update);
}
