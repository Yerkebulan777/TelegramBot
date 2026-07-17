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
        var now = DateTime.UtcNow;
        if (_sessions.TryGetValue(userId, out var existing) &&
            now - existing.LastActivity > _sessionTimeout)
        {
            _ = TryRemoveExpiredSessionAndLockIfIdle(userId, now);
        }

        var session = _sessions.GetOrAdd(userId, key => new UserSession
        {
            UserId = key,
            LastActivity = now
        });

        session.LastActivity = DateTime.UtcNow;
        return session;
    }

    /// <summary>
    /// Асинхронно захватывает блокировку на доступ к сессии пользователя.
    /// </summary>
    /// <remarks>
    /// Семафор намеренно НЕ удаляется из <c>_sessionLocks</c> при истечении сессии: между
    /// <c>GetOrAdd</c> и <c>WaitAsync</c> cleanup мог успеть <c>TryRemove + Dispose</c>, что приводило
    /// к <see cref="ObjectDisposedException"/> и, хуже, к созданию нового семафора для того же
    /// пользователя — нарушению per-user взаимоисключения. Ленивое удержание семафоров безопасно:
    /// их размер мал, а число пользователей ограничено.
    /// </remarks>
    public async Task<IDisposable> AcquireUserLockAsync(long userId)
    {
        var sessionLock = _sessionLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync();
        RemoveExpiredSessionValue(userId, DateTime.UtcNow);
        return new SessionLockReleaser(sessionLock);
    }

    private void CleanUpExpiredSessions(CancellationToken cancellationToken)
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

            _ = TryRemoveExpiredSessionAndLockIfIdle(key, now, cancellationToken);
        }

    }

    private void RemoveExpiredSessionValue(long userId, DateTime now)
    {
        if (_sessions.TryGetValue(userId, out var session) &&
            now - session.LastActivity > _sessionTimeout)
        {
            _ = _sessions.TryRemove(userId, out _);
        }
    }

    private bool TryRemoveExpiredSessionAndLockIfIdle(long userId, DateTime now, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(userId, out var session) ||
            now - session.LastActivity <= _sessionTimeout)
        {
            return false;
        }

        // Семафор не удаляем (см. комментарий в AcquireUserLockAsync): оставляем его в словаре,
        // чтобы исключить гонку с dispose. Если он занят — сессией пользуются прямо сейчас, не трогаем.
        if (!_sessionLocks.TryGetValue(userId, out var sessionLock))
        {
            return _sessions.TryRemove(userId, out _);
        }

        if (!sessionLock.Wait(0, cancellationToken))
        {
            return false;
        }

        try
        {
            if (!_sessions.TryGetValue(userId, out var candidate) ||
                now - candidate.LastActivity <= _sessionTimeout)
            {
                return false;
            }

            return _sessions.TryRemove(userId, out _);
        }
        finally
        {
            try { _ = sessionLock.Release(); }
            catch (ObjectDisposedException) { /* already disposed */ }
            catch (SemaphoreFullException) { /* already released */ }
        }
    }

    private async Task RunCleanupLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _cleanupTimer.WaitForNextTickAsync(cancellationToken))
            {
                CleanUpExpiredSessions(cancellationToken);
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
