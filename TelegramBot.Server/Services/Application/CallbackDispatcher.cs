using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;

namespace TelegramBot.Server.Services.Application;

/// <summary>
/// Диспетчер callback-запросов с кэшированным маппингом prefix → handler.
/// Оптимизация: O(1) поиск вместо линейного перебора всех хендлеров.
/// </summary>
public sealed class CallbackDispatcher(IEnumerable<ICallbackHandler> handlers, ILogger<CallbackDispatcher> logger)
{
    private readonly Dictionary<string, ICallbackHandler> _handlerMap =
        handlers
            .SelectMany(handler => handler.GetSupportedPrefixes().Select(prefix => (prefix, handler)))
            .ToDictionary(item => item.prefix, item => item.handler);

    /// <summary>
    /// Отправляет callback соответствующему хендлеру.
    /// </summary>
    public async Task DispatchAsync(CallbackContext context, CancellationToken cancellationToken = default)
    {
        if (!_handlerMap.TryGetValue(context.ParsedCallback.Prefix, out var handler))
        {
            logger.LogDebug("Callback ignored: prefix={Prefix}", context.ParsedCallback.Prefix);
            return;
        }

        try
        {
            await handler.HandleAsync(context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in handler {HandlerName}", handler.GetType().Name);
        }
    }
}
