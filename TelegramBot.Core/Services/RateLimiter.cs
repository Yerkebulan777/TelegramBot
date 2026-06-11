using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using TelegramBot.Core.Config;

namespace TelegramBot.Core.Services;

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

        lock (requestWindow.SyncRoot)
        {
            // Очистка expired записей в той же критической секции
            while (requestWindow.Timestamps.TryPeek(out var timestamp) && now - timestamp > _window)
            {
                _ = requestWindow.Timestamps.TryDequeue();
            }

            // Проверка лимита и добавление нового timestamp
            if (requestWindow.Timestamps.Count >= _maxRequests)
            {
                return false;
            }
            
            requestWindow.Timestamps.Enqueue(now);
            
            // Удаляем entry из словаря, если очередь пуста (оптимизация памяти)
            if (requestWindow.Timestamps.IsEmpty)
            {
                _ = ((ICollection<KeyValuePair<long, RequestWindow>>)_requests).Remove(
                    new KeyValuePair<long, RequestWindow>(userId, requestWindow));
            }
            
            return true;
        }
    }

    private sealed class RequestWindow
    {
        public ConcurrentQueue<DateTime> Timestamps { get; } = new();
        public object SyncRoot { get; } = new object();
    }
}
