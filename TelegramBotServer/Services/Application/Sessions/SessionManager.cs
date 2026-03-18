using System.Collections.Concurrent;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services
{
    public class SessionManager : ISessionManager, IDisposable
    {
        private readonly ConcurrentDictionary<long, UserSession> _sessions = new();
        private readonly ConcurrentDictionary<long, SemaphoreSlim> _sessionLocks = new();
        private readonly TimeSpan _sessionTimeout;
        private readonly Timer _cleanupTimer;

        public SessionManager(TimeSpan sessionTimeout)
        {
            _sessionTimeout = sessionTimeout;
            // Run cleanup once per timeout interval instead of on every message
            _cleanupTimer = new Timer(
                _ => CleanUpExpiredSessions(),
                null,
                sessionTimeout,
                sessionTimeout);
        }

    /// <summary>
    /// Возвращает существующую сессию пользователя или создает новую.
    /// </summary>
    public UserSession GetOrCreateSession(long userId)
        {
            var session = _sessions.GetOrAdd(userId,
                _ => new UserSession { UserId = userId });

            session.LastActivity = DateTime.UtcNow;
            return session;
        }

    /// <summary>
    /// Получает блокировку для безопасной работы с сессией пользователя.
    /// </summary>
    public async Task<IDisposable> AcquireUserLockAsync(long userId)
        {
            var sessionLock = _sessionLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
            await sessionLock.WaitAsync();
            return new SessionLockReleaser(sessionLock);
        }

    /// <summary>
    /// Удаляет сессию пользователя из памяти.
    /// </summary>
    public void RemoveSession(long userId)
        {
            _sessions.TryRemove(userId, out _);
            if (_sessionLocks.TryRemove(userId, out var sl))
                sl.Dispose();
        }

        private void CleanUpExpiredSessions()
        {
            var now = DateTime.UtcNow;

            foreach (var key in _sessions.Keys.ToList())
            {
                if (!_sessions.TryGetValue(key, out var session) || now - session.LastActivity <= _sessionTimeout)
                    continue;

                // Try to acquire the lock without blocking — skip if the user is active
                if (!_sessionLocks.TryGetValue(key, out var sessionLock))
                {
                    _sessions.TryRemove(key, out _);
                    continue;
                }

                if (!sessionLock.Wait(0))
                    continue;

                // Track whether we disposed the semaphore inside the lock, so we know not to Release() it.
                bool removed = false;
                try
                {
                    // Double-check after acquiring lock
                    if (_sessions.TryGetValue(key, out var candidate) && now - candidate.LastActivity > _sessionTimeout)
                    {
                        _sessions.TryRemove(key, out _);
                        if (_sessionLocks.TryRemove(key, out var removedLock))
                        {
                            removedLock.Dispose();
                            removed = true;
                        }
                    }
                }
                finally
                {
                    // Release only if the semaphore was NOT disposed above.
                    // If removed=true, the semaphore is already disposed — calling Release() would throw ObjectDisposedException.
                    if (!removed)
                        sessionLock.Release();
                }
            }
        }

        public void Dispose()
        {
            _cleanupTimer.Dispose();
            foreach (var sl in _sessionLocks.Values)
                sl.Dispose();
            _sessionLocks.Clear();
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
