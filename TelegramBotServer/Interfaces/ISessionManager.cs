using TelegramBotServer.Models;

namespace TelegramBotServer.Interfaces
{
    public interface ISessionManager
    {
        UserSession GetOrCreateSession(long userId);
        Task<IDisposable> AcquireUserLockAsync(long userId);
        void RemoveSession(long userId);
    }
}
