using System.Collections.Concurrent;
using TelegramBot.Core.Models;

namespace TelegramBot.Server.Services.Application;

/// <summary>
/// Менеджер сессий пользователей с потокобезопасной блокировкой и очисткой.
/// Исправлена утечка памяти _sessionLocks: семафоры удаляются при RemoveSession.
/// </summary>
public class SessionManager : IDisposable
{
    // Фоновая очистка раз в 30 минут — основной cleanup идёт лениво при доступе
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<long, UserSession> _sessions = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _sessionLocks = new();
    private readonly TimeSpan _sessionTimeout;
    private readonly PeriodicTimer _cleanupTimer;
    private readonly CancellationTokenSource _cleanupCts = new();
    private readonly ILogger<SessionManager>? _logger;

    public SessionManager(TimeSpan sessionTimeout, ILogger<SessionManager>? logger = null)
    {
        _sessionTimeout = sessionTimeout;
        _logger = logger;
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
        return new SessionLockReleaser(sessionLock, this, userId);
    }

    /// <summary>
    /// Удаляет сессию и освобождает ресурсы (семафор).
    /// </summary>
    public void RemoveSession(long userId)
    {
        _ = _sessions.TryRemove(userId, out _);

        if (_sessionLocks.TryRemove(userId, out var semaphore))
        {
            if (semaphore.CurrentCount >= 1)
            {
                try
                {
                    semaphore.Dispose();
                    _logger?.LogDebug("Session lock disposed for user {UserId}", userId);
                }
                catch (ObjectDisposedException)
                {
                    _logger?.LogTrace("Session lock already disposed for user {UserId}", userId);
                }
            }
            else
            {
                _ = _sessionLocks.TryAdd(userId, semaphore);
                _logger?.LogDebug("Session lock still in use for user {UserId}, deferred cleanup", userId);
            }
        }
    }

    private async Task CleanUpExpiredSessionsAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var cleanedCount = 0;

        foreach (var key in _sessions.Keys.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_sessions.TryGetValue(key, out var session))
            {
                continue;
            }

            if (now - session.LastActivity <= _sessionTimeout)
            {
                continue;
            }

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
                    cleanedCount++;
                }
            }
            finally
            {
                if (lockRemoved)
                {
                    try
                    {
                        sessionLock.Dispose();
                    }
                    catch (ObjectDisposedException ex)
                    {
                        _logger?.LogTrace(ex, "Session lock already disposed during cleanup for user {UserId}", key);
                    }
                }
                else
                {
                    try
                    {
                        _ = sessionLock.Release();
                    }
                    catch (ObjectDisposedException ex)
                    {
                        _logger?.LogTrace(ex, "Session lock already disposed on release for user {UserId}", key);
                    }
                    catch (SemaphoreFullException ex)
                    {
                        _logger?.LogTrace(ex, "Session lock already at max count for user {UserId}", key);
                    }
                }
            }
        }

        if (cleanedCount > 0)
        {
            _logger?.LogInformation("Cleaned up {Count} expired sessions", cleanedCount);
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
            try
            {
                semaphore.Dispose();
            }
            catch (ObjectDisposedException ex)
            {
                _logger?.LogTrace(ex, "Session lock already disposed for user {UserId} during shutdown", userId);
            }
        }
        _sessionLocks.Clear();
    }

    private sealed class SessionLockReleaser : IDisposable
    {
        private readonly SemaphoreSlim _sessionLock;
        private int _disposed;

        public SessionLockReleaser(SemaphoreSlim sessionLock, SessionManager _, long __)
        {
            _sessionLock = sessionLock;
            _disposed = 0;
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
            {
                try
                {
                    _ = _sessionLock.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Семафор уже Disposed (shutdown снёс его в SessionManager.Dispose).
                }
                catch (SemaphoreFullException)
                {
                    // Release() вызван повторно — для SemaphoreSlim не должно случаться,
                    // но на всякий случай проглатываем как уже-учтённое.
                }
            }
        }
    }
}
