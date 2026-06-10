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

        requestWindow.Timestamps.Enqueue(now);

        if (requestWindow.Timestamps.Count > _maxRequests)
        {
            CleanupExpired(requestWindow, now);
        }

        return requestWindow.Timestamps.Count <= _maxRequests;
    }

    private void CleanupExpired(RequestWindow requestWindow, DateTime now)
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
    }
}
