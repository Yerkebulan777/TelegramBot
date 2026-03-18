using TelegramBotServer.Models;

namespace TelegramBotServer.Interfaces
{
    public interface ISessionManager
    {
        /// <summary>Возвращает существующую сессию или создает новую.</summary>
        UserSession GetOrCreateSession(long userId);
        /// <summary>Получает блокировку для безопасной работы с сессией.</summary>
        Task<IDisposable> AcquireUserLockAsync(long userId);
        /// <summary>Удаляет сессию пользователя.</summary>
        void RemoveSession(long userId);
    }
}
