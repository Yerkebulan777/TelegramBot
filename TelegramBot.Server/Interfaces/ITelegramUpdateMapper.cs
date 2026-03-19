using Telegram.Bot.Types;

namespace TelegramBot.Server.Interfaces;

/// <summary>
/// Maps Telegram updates to application DTOs.
/// </summary>
public interface ITelegramUpdateMapper
{
    /// <summary>Преобразует обновление Telegram в DTO.</summary>
    Task<object?> Map(Update update);
}
