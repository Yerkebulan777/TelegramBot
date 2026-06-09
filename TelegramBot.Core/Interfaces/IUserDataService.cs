using TelegramBot.Core.Models;

namespace TelegramBot.Core.Interfaces;

/// <summary>
/// Service for user data persistence.
/// </summary>
public interface IUserDataService
{
    /// <summary>Возвращает запись пользователя или null.</summary>
    Task<BotUser?> GetUserAsync(long userId);

    /// <summary>Создаёт или обновляет пользователя.</summary>
    Task UpsertUserAsync(BotUser user);

    /// <summary>Массовая вставка/обновление пользователей.</summary>
    Task UpsertUsersBatchAsync(long[] userIds, int role, int status);
}
