using System.Collections.Concurrent;
using TelegramBot.Core.Models;

namespace TelegramBot.Server.Services.Application;

/// <summary>
/// Менеджер сессий пользователей с потокобезопасной блокировкой и фоновой очисткой.
/// </summary>
public class SessionManager : IDisposable
{
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

    /// <summary>
    /// Получает или создаёт сессию пользователя.
    /// Lazy cleanup: если сессия существует и истекла — удаляем из _sessions.
    /// </summary>
    public UserSession GetOrCreateSession(long userId)
    {
        if (_sessions.TryGetValue(userId, out var existing) &&
            DateTime.UtcNow - existing.LastActivity > _sessionTimeout)
        {
            _ = _sessions.TryRemove(userId, out _);
        }

        var session = _sessions.GetOrAdd(userId, key => new UserSession
        {
            UserId = key,
            LastActivity = DateTime.UtcNow
        });

        session.LastActivity = DateTime.UtcNow;
        return session;
    }

    /// <summary>
    /// Асинхронно захватывает блокировку на доступ к сессии пользователя.
    /// </summary>
    public async Task<IDisposable> AcquireUserLockAsync(long userId)
    {
        var sessionLock = _sessionLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync();
        return new SessionLockReleaser(sessionLock);
    }

    private async Task CleanUpExpiredSessionsAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        foreach (var key in _sessions.Keys.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_sessions.TryGetValue(key, out var session) ||
                now - session.LastActivity <= _sessionTimeout)
            {
                continue;
            }

            if (!_sessionLocks.TryGetValue(key, out var sessionLock) ||
                !await sessionLock.WaitAsync(0, cancellationToken))
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
                    try { sessionLock.Dispose(); }
                    catch (ObjectDisposedException) { /* already disposed */ }
                }
                else
                {
                    try { _ = sessionLock.Release(); }
                    catch (ObjectDisposedException) { /* already disposed */ }
                    catch (SemaphoreFullException) { /* already released */ }
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

        foreach (var (userId, semaphore) in _sessionLocks)
        {
            try { semaphore.Dispose(); }
            catch (ObjectDisposedException) { /* already disposed */ }
        }
        _sessionLocks.Clear();
    }

    private sealed class SessionLockReleaser(SemaphoreSlim sessionLock) : IDisposable
    {
        public void Dispose()
        {
            try { _ = sessionLock.Release(); }
            catch (ObjectDisposedException) { /* already disposed */ }
            catch (SemaphoreFullException) { /* already released */ }
        }
    }
}
