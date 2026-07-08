using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using TelegramBot.Core.Config;

namespace TelegramBot.Core.Helpers;

/// <summary>
/// Rate limiter с sliding window для ограничения количества запросов от пользователя.
/// Исправлены race conditions: очистка и проверка выполняются в единой критической секции.
/// </summary>
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

    /// <summary>
    /// Проверяет, разрешён ли запрос от пользователя.
    /// Атомарная операция: очистка expired записей и добавление новой выполняются под lock.
    /// </summary>
    public bool IsAllowed(long userId)
    {
        var now = DateTime.UtcNow;
        var requestWindow = _requests.GetOrAdd(userId, static _ => new RequestWindow());

        lock (requestWindow)
        {
            // Очистка expired записей в той же критической секции
            while (requestWindow.Timestamps.Count > 0 && now - requestWindow.Timestamps.Peek() > _window)
            {
                _ = requestWindow.Timestamps.Dequeue();
            }

            // Проверка лимита и добавление нового timestamp
            if (requestWindow.Timestamps.Count >= _maxRequests)
            {
                return false;
            }

            requestWindow.Timestamps.Enqueue(now);

            return true;
        }
    }

    private sealed class RequestWindow
    {
        public Queue<DateTime> Timestamps { get; } = new();
    }
}
