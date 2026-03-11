using System.Collections.Concurrent;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services
{
    public class SessionManager: ISessionManager
    {
        private readonly ConcurrentDictionary<long, UserSession> _sessions = new();
        private readonly TimeSpan _sessionTimeout;

        public SessionManager(TimeSpan sessionTimeout)
        {
            _sessionTimeout = sessionTimeout;
        }

        public UserSession GetOrCreateSession(long userId)
        {
            CleanUpExpiredSessions();

            //if (!_sessions.TryGetValue(userId, out var session))
            //{
            //    session = new UserSession { UserId = userId };
            //    _sessions[userId] = session;
            //}

            var session = _sessions.GetOrAdd(userId,
                _ => new UserSession { UserId = userId });


            session.LastActivity = DateTime.UtcNow;
            return session;
        }

        public void RemoveSession(long userId)
        {
            _sessions.TryRemove(userId, out _);
        }

        private void CleanUpExpiredSessions()
        {
            var now = DateTime.UtcNow;

            var expired = _sessions
                .Where(kv=>now - kv.Value.LastActivity>_sessionTimeout)
                .Select(kv => kv.Key)
                .ToList();

            foreach(var key in expired)
            {
                _sessions.TryRemove(key, out _);
            }
        }
    }
}
