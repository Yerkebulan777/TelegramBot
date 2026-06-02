using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;

namespace TelegramBot.Server.Services.Application;

/// <summary>
/// Dispatches callback queries to appropriate handlers.
/// Implements Chain of Responsibility pattern.
/// </summary>
public sealed class CallbackDispatcher(IEnumerable<ICallbackHandler> handlers, ILogger<CallbackDispatcher> logger) : ICallbackDispatcher
{
    private readonly IEnumerable<ICallbackHandler> _handlers = handlers.OrderBy(h => h.Priority).ToList();
    private readonly ILogger<CallbackDispatcher> _logger = logger;

    /// <summary>
    /// Перенаправляет callback на первый подходящий обработчик (Chain of Responsibility).
    /// </summary>
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
                    // Continue processing - don't rethrow to prevent stopping update handling
                }
            }
        }

        _logger.LogDebug(
            "No handler found for callback '{Prefix}' from user {Username} ({UserId})",
            context.ParsedCallback.Prefix,
            context.Username,
            context.UserId);

        return false;
    }
}
