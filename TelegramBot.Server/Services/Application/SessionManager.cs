using System.Collections.Concurrent;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;

namespace TelegramBot.Server.Services.Application;

public class SessionManager : ISessionManager, IDisposable
{
    private readonly ConcurrentDictionary<long, UserSession> _sessions = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _sessionLocks = new();
    private readonly TimeSpan _sessionTimeout;
    private readonly Timer _cleanupTimer;

    public SessionManager(TimeSpan sessionTimeout)
    {
        _sessionTimeout = sessionTimeout;
        _cleanupTimer = new Timer(
            _ => CleanUpExpiredSessions(),
            null,
            sessionTimeout,
            sessionTimeout);
    }

    public UserSession GetOrCreateSession(long userId)
    {
        var session = _sessions.GetOrAdd(userId, key => new UserSession { UserId = key });
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
        _=_sessions.TryRemove(userId, out _);
    }

    private void CleanUpExpiredSessions()
    {
        var now = DateTime.UtcNow;

        foreach (var (key, session) in _sessions)
        {
            if (now - session.LastActivity <= _sessionTimeout)
            {
                continue;
            }

            if (!_sessionLocks.TryGetValue(key, out var sessionLock) || !sessionLock.Wait(0))
            {
                _=_sessions.TryRemove(key, out _);
                continue;
            }

            try
            {
                if (_sessions.TryGetValue(key, out var candidate) && now - candidate.LastActivity > _sessionTimeout)
                {
                    _=_sessions.TryRemove(key, out _);
                }
            }
            finally
            {
                _=sessionLock.Release();
            }
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        _sessionLocks.Clear();
    }

    private sealed class SessionLockReleaser(SemaphoreSlim sessionLock) : IDisposable
    {
        public void Dispose()
        {
            _=sessionLock.Release();
        }
    }
}
