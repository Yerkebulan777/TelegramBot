using System.Collections.Concurrent;
using TelegramBot.Core.Models;

namespace TelegramBot.Server.Services.Application;

public class SessionManager : IDisposable
{
    // Фоновая очистка раз в 30 минут — основной cleanup идёт лениво при доступе
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<long, UserSession> _sessions = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _sessionLocks = new();
    private readonly TimeSpan _sessionTimeout;
    private readonly PeriodicTimer _cleanupTimer;
    private readonly CancellationTokenSource _cleanupCts = new();

    public SessionManager(TimeSpan sessionTimeout)
    {
        _sessionTimeout = sessionTimeout;
        _cleanupTimer = new PeriodicTimer(CleanupInterval);
        _ = RunCleanupLoopAsync(_cleanupCts.Token);
    }

    public UserSession GetOrCreateSession(long userId)
    {
        // Lazy cleanup: если сессия существует и истекла — удаляем из _sessions.
        // НЕ трогаем _sessionLocks — семафор всё ещё может удерживаться текущим потоком
        // через AcquireUserLockAsync. Dispose() семафора, удерживаемого вызвавшим потоком,
        // приводит к ObjectDisposedException при Release() в SessionLockReleaser.Dispose().
        if (_sessions.TryGetValue(userId, out var existing) &&
            DateTime.UtcNow - existing.LastActivity > _sessionTimeout)
        {
            _ = _sessions.TryRemove(userId, out _);
            // _sessionLocks НЕ удаляем и НЕ диспозим — cleanup выполняется в фоновом CleanUpExpiredSessionsAsync
        }

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
        _ = _sessions.TryRemove(userId, out _);
        // НЕ диспозим семафор из _sessionLocks — он может удерживаться другим потоком.
        // Фоновая CleanUpExpiredSessionsAsync безопасно обрабатывает и locks и sessions.
        _ = _sessionLocks.TryRemove(userId, out _);
    }

    private async Task CleanUpExpiredSessionsAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        foreach (var key in _sessions.Keys.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_sessions.TryGetValue(key, out var session))
                continue;

            if (now - session.LastActivity <= _sessionTimeout)
                continue;

            if (!_sessionLocks.TryGetValue(key, out var sessionLock) || !await sessionLock.WaitAsync(0, cancellationToken))
            {
                continue;
            }

            var lockRemoved = false;
            try
            {
                if (_sessions.TryGetValue(key, out var candidate) && now - candidate.LastActivity > _sessionTimeout)
                {
                    _ = _sessions.TryRemove(key, out _);
                    lockRemoved = _sessionLocks.TryRemove(key, out _);
                }
            }
            finally
            {
                if (lockRemoved)
                {
                    sessionLock.Dispose();
                }
                else
                {
                    _ = sessionLock.Release();
                }
            }
        }
    }

    private async Task RunCleanupLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _cleanupTimer.WaitForNextTickAsync(cancellationToken))
            {
                await CleanUpExpiredSessionsAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested
        }
    }

    public void Dispose()
    {
        _cleanupCts.Cancel();
        _cleanupCts.Dispose();
        _cleanupTimer.Dispose();
        foreach (var (_, semaphore) in _sessionLocks)
        {
            semaphore.Dispose();
        }
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
