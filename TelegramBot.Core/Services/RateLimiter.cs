using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using TelegramBot.Core.Config;

namespace TelegramBot.Core.Services;

public sealed class RateLimiter
{
    private readonly ConcurrentDictionary<long, RequestWindow> _requests = new();
    private readonly int _maxRequests;
    private readonly TimeSpan _window;

    public RateLimiter(IOptions<RateLimitOptions> options)
    {
        _maxRequests = options.Value.MaxRequests;
        _window = TimeSpan.FromSeconds(options.Value.WindowSeconds);
    }

    public bool IsAllowed(long userId)
    {
        var now = DateTime.UtcNow;
        var requestWindow = _requests.GetOrAdd(userId, static _ => new RequestWindow());

        // Сначала очищаем expired записи, прежде чем добавлять новую
        CleanupExpired(requestWindow, now, userId);

        // Добавляем timestamp только если после очистки есть место
        // Используем lock-free подход с проверкой через Count после очистки
        lock (requestWindow.SyncRoot)
        {
            if (requestWindow.Timestamps.Count >= _maxRequests)
            {
                return false;
            }
            
            requestWindow.Timestamps.Enqueue(now);
            return true;
        }
    }

    private void CleanupExpired(RequestWindow requestWindow, DateTime now, long userId)
    {
        if (Interlocked.CompareExchange(ref requestWindow.CleanupInProgress, 1, 0) != 0)
        {
            return;
        }

        try
        {
            while (requestWindow.Timestamps.TryPeek(out var timestamp) && now - timestamp > _window)
            {
                _ = requestWindow.Timestamps.TryDequeue(out _);
            }

            // Если очередь пуста — убираем entry из словаря
            // KeyValuePair.Remove гарантирует, что удаляем только если entry всё ещё принадлежит этому userId
            if (requestWindow.Timestamps.IsEmpty)
            {
                _ = ((ICollection<KeyValuePair<long, RequestWindow>>)_requests).Remove(
                    new KeyValuePair<long, RequestWindow>(userId, requestWindow));
            }
        }
        finally
        {
            _ = Interlocked.Exchange(ref requestWindow.CleanupInProgress, 0);
        }
    }

    private sealed class RequestWindow
    {
        public ConcurrentQueue<DateTime> Timestamps { get; } = new();
        public int CleanupInProgress;
        public object SyncRoot { get; } = new object();
    }
}
