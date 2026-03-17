using TelegramBotServer.Interfaces;

namespace TelegramBotServer.Services.Application;

/// <summary>
/// Dispatches callback queries to appropriate handlers.
/// Implements Chain of Responsibility pattern.
/// </summary>
public sealed class CallbackDispatcher
{
    private readonly IEnumerable<ICallbackHandler> _handlers;
    private readonly ILogger<CallbackDispatcher> _logger;

    public CallbackDispatcher(
        IEnumerable<ICallbackHandler> handlers,
        ILogger<CallbackDispatcher> logger)
    {
        // Sort handlers by priority (lower values first)
        _handlers = handlers.OrderBy(h => h.Priority).ToList();
        _logger = logger;
    }

    /// <summary>
    /// Dispatches the callback to the first handler that can process it.
    /// </summary>
    /// <param name="context">The callback context.</param>
    /// <param name="cancellationToken">Token for cancellation.</param>
    /// <returns>True if the callback was handled.</returns>
    public async Task<bool> DispatchAsync(CallbackContext context, CancellationToken cancellationToken = default)
    {
        foreach (var handler in _handlers)
        {
            if (handler.CanHandle(context.ParsedCallback.Prefix))
            {
                _logger.LogDebug(
                    "Dispatching callback '{Prefix}' to {HandlerName}",
                    context.ParsedCallback.Prefix,
                    handler.GetType().Name);

                try
                {
                    var handled = await handler.HandleAsync(context, cancellationToken);
                    if (handled)
                    {
                        _logger.LogDebug(
                            "Callback '{Prefix}' handled by {HandlerName}",
                            context.ParsedCallback.Prefix,
                            handler.GetType().Name);
                        return true;
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation(
                        "Callback '{Prefix}' handling was cancelled",
                        context.ParsedCallback.Prefix);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Error in handler {HandlerName} for callback '{Prefix}'",
                        handler.GetType().Name,
                        context.ParsedCallback.Prefix);
                    throw;
                }
            }
        }

        _logger.LogDebug(
            "No handler found for callback '{Prefix}' from user {UserId}",
            context.ParsedCallback.Prefix,
            context.UserId);

        return false;
    }
}
