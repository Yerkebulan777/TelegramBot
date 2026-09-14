using TelegramBot.Core.Models;
using TelegramBot.Server.Services.Application.Handlers;

namespace TelegramBot.Server.Services.Application;

/// <summary>
/// Диспетчер callback-запросов с кэшированным маппингом prefix → handler.
/// Оптимизация: O(1) поиск вместо линейного перебора всех хендлеров.
/// </summary>
public sealed class CallbackDispatcher(IEnumerable<CallbackHandlerBase> handlers, ILogger<CallbackDispatcher> logger)
{
    private readonly Dictionary<string, CallbackHandlerBase> _handlerMap =
        handlers
            .SelectMany(handler => handler.SupportedPrefixes.Select(prefix => (prefix, handler)))
            .ToDictionary(item => item.prefix, item => item.handler);

    /// <summary>
    /// Отправляет callback соответствующему хендлеру.
    /// </summary>
    public Task DispatchAsync(CallbackContext context, CancellationToken cancellationToken = default)
    {
        if (!_handlerMap.TryGetValue(context.ParsedCallback.Prefix, out var handler))
        {
            logger.LogDebug("Callback ignored: prefix={Prefix}", context.ParsedCallback.Prefix);
            return Task.CompletedTask;
        }

        return handler.HandleAsync(context, cancellationToken);
    }
}
