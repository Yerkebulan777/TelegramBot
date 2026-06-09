using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using TelegramBot.Core.Config;

namespace TelegramBot.Core.Services;

public sealed class RateLimiter
{
    private readonly ConcurrentDictionary<long, List<DateTime>> _requests = new();
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
        var timestamps = _requests.GetOrAdd(userId, static _ => []);

        lock (timestamps)
        {
            _=timestamps.RemoveAll(t => now - t > _window);
            timestamps.Add(now);
            return timestamps.Count <= _maxRequests;
        }
    }
}
