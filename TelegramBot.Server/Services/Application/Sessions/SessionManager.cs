using System.Collections.Concurrent;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;

namespace TelegramBot.Server.Services.Application.Sessions;

public class SessionManager : ISessionManager, IDisposable
{
    private readonly ConcurrentDictionary<long, UserSession> _sessions = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _sessionLocks = new();
    private readonly TimeSpan _sessionTimeout;
    private readonly IDataService _dataService;
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    public SessionManager(TimeSpan sessionTimeout, IDataService dataService)
    {
        _sessionTimeout = sessionTimeout;
        _dataService = dataService;
        _cleanupTimer = new Timer(
            _ => CleanUpExpiredSessions(),
            null,
            sessionTimeout,
            sessionTimeout);
    }

    public UserSession GetOrCreateSession(long userId)
    {
        var session = _sessions.GetOrAdd(userId, _ => new UserSession { UserId = userId });
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
        if (_sessionLocks.TryRemove(userId, out var sl))
            sl.Dispose();
    }

    private void CleanUpExpiredSessions()
    {
        if (_disposed)
            return;

        var now = DateTime.UtcNow;

        foreach (var key in _sessions.Keys.ToList())
        {
            if (!_sessions.TryGetValue(key, out var session) || now - session.LastActivity <= _sessionTimeout)
                continue;

            if (!_sessionLocks.TryGetValue(key, out var sessionLock))
            {
                _sessions.TryRemove(key, out _);
                continue;
            }

            if (!sessionLock.Wait(0))
                continue;

            try
            {
                if (_disposed)
                {
                    sessionLock.Release();
                    return;
                }

                if (_sessions.TryGetValue(key, out var candidate) && now - candidate.LastActivity > _sessionTimeout)
                {
                    _sessions.TryRemove(key, out _);
                    if (_sessionLocks.TryRemove(key, out var removedLock))
                        removedLock.Dispose();

                    var messageIds = candidate.TakeTrackedMessages();
                    if (messageIds.Count > 0)
                        _ = Task.Run(() => _dataService.SaveTrackedMessagesAsync(key, messageIds));
                }
            }
            finally
            {
                if (!_disposed)
                    sessionLock.Release();
            }
        }

        // Clean up orphaned locks (locks for users without sessions)
        foreach (var key in _sessionLocks.Keys.ToList())
        {
            if (_disposed)
                return;

            if (!_sessions.ContainsKey(key))
            {
                if (_sessionLocks.TryRemove(key, out var orphanedLock))
                    orphanedLock.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cleanupTimer.Dispose();
        foreach (var sl in _sessionLocks.Values)
            sl.Dispose();
        _sessionLocks.Clear();
    }

    private sealed class SessionLockReleaser(SemaphoreSlim sessionLock) : IDisposable
    {
        public void Dispose() => sessionLock.Release();
    }
}
