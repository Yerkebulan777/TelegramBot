using TelegramBotServer.Models;

namespace TelegramBotServer.Interfaces
{
    public interface ISessionManager
    {
        UserSession GetOrCreateSession(long userId);
        void RemoveSession(long userId);
    }
}
