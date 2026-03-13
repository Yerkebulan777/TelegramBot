using System.Collections.Concurrent;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services
{
    public class SessionManager : ISessionManager
    {
        private readonly ConcurrentDictionary<long, UserSession> _sessions = new();
        private readonly ConcurrentDictionary<long, SemaphoreSlim> _sessionLocks = new();
        private readonly object _cleanupLock = new();
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

        public async Task<IDisposable> AcquireUserLockAsync(long userId)
        {
            var sessionLock = _sessionLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
            await sessionLock.WaitAsync();
            return new SessionLockReleaser(sessionLock);
        }

        public void RemoveSession(long userId)
        {
            _sessions.TryRemove(userId, out _);
            _sessionLocks.TryRemove(userId, out _);
        }

        private void CleanUpExpiredSessions()
        {
            lock (_cleanupLock)
            {
                var now = DateTime.UtcNow;

                foreach (var key in _sessions.Keys.ToList())
                {
                    if (!_sessions.TryGetValue(key, out var session) || now - session.LastActivity <= _sessionTimeout)
                    {
                        continue;
                    }

                    _sessionLocks.TryGetValue(key, out var sessionLock);
                    if (sessionLock != null && !sessionLock.Wait(0))
                    {
                        continue;
                    }

                    try
                    {
                        if (_sessions.TryGetValue(key, out var candidate) && now - candidate.LastActivity > _sessionTimeout)
                        {
                            _sessions.TryRemove(key, out _);
                            _sessionLocks.TryRemove(key, out _);
                        }
                    }
                    finally
                    {
                        sessionLock?.Release();
                    }
                }
            }
        }

        private sealed class SessionLockReleaser : IDisposable
        {
            private readonly SemaphoreSlim _sessionLock;

            public SessionLockReleaser(SemaphoreSlim sessionLock)
            {
                _sessionLock = sessionLock;
            }

            public void Dispose()
            {
                _sessionLock.Release();
            }
        }
    }
}
