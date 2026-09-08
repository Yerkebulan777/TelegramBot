using Microsoft.Extensions.Options;
using TelegramBot.Core.Config;

namespace TelegramBot.Core.Helpers;

/// <summary>
/// Rate limiter с sliding window для ограничения количества запросов от пользователя.
/// Очистка, проверка и добавление выполняются в единой критической секции.
/// </summary>
public sealed class RateLimiter
{
    private const int CleanupEveryRequests = 1024;

    // ponytail: global lock is enough for 10 parallel updates; shard by user if this becomes hot.
    private readonly object _syncRoot = new();
    private readonly Dictionary<long, Queue<DateTime>> _requests = new();
    private readonly Dictionary<long, DateTime> _lastWarnings = new();
    private readonly int _maxRequests;
    private readonly TimeSpan _window;
    private long _requestsSinceCleanup;

    public RateLimiter(IOptions<RateLimitOptions> options)
    {
        _maxRequests = options.Value.MaxRequests;
        _window = TimeSpan.FromSeconds(options.Value.WindowSeconds);
    }

    /// <summary>
    /// Проверяет, разрешён ли запрос от пользователя.
    /// Атомарная операция: очистка expired записей и добавление новой выполняются под lock.
    /// shouldNotify разрешает одно предупреждение за WindowSeconds для каждого пользователя.
    /// </summary>
    public bool IsAllowed(long userId, out bool shouldNotify)
    {
        var now = DateTime.UtcNow;
        shouldNotify = false;

        lock (_syncRoot)
        {
            _requestsSinceCleanup++;
            if (_requestsSinceCleanup >= CleanupEveryRequests)
            {
                CleanupExpiredWindows(now);
                _requestsSinceCleanup = 0;
            }

            if (!_requests.TryGetValue(userId, out var timestamps))
            {
                timestamps = new Queue<DateTime>();
                _requests[userId] = timestamps;
            }

            RemoveExpired(timestamps, now);

            if (timestamps.Count >= _maxRequests)
            {
                if (!_lastWarnings.TryGetValue(userId, out var lastWarning) || now - lastWarning >= _window)
                {
                    _lastWarnings[userId] = now;
                    shouldNotify = true;
                }
                return false;
            }

            timestamps.Enqueue(now);

            return true;
        }
    }

    private void CleanupExpiredWindows(DateTime now)
    {
        foreach (var (userId, lastWarning) in _lastWarnings.ToArray())
        {
            if (now - lastWarning >= _window)
            {
                _ = _lastWarnings.Remove(userId);
            }
        }

        foreach (var (userId, timestamps) in _requests.ToArray())
        {
            RemoveExpired(timestamps, now);
            if (timestamps.Count == 0)
            {
                _ = _requests.Remove(userId);
            }
        }
    }

    private void RemoveExpired(Queue<DateTime> timestamps, DateTime now)
    {
        while (timestamps.Count > 0 && now - timestamps.Peek() > _window)
        {
            _ = timestamps.Dequeue();
        }
    }
}
