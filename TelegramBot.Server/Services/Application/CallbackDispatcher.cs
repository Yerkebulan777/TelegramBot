using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;

namespace TelegramBot.Server.Services.Application;

public sealed class CallbackDispatcher(IEnumerable<ICallbackHandler> handlers, ILogger<CallbackDispatcher> logger) : ICallbackDispatcher
{
    private readonly IEnumerable<ICallbackHandler> _handlers = handlers.OrderBy(h => h.Priority).ToList();

    public async Task<bool> DispatchAsync(CallbackContext context, CancellationToken cancellationToken = default)
    {
        foreach (var handler in _handlers)
        {
            if (handler.CanHandle(context.ParsedCallback.Prefix))
            {
                logger.LogDebug(
                    "Callback dispatch: prefix={Prefix}, handler={HandlerName}, user={UserId}",
                    context.ParsedCallback.Prefix,
                    handler.GetType().Name,
                    context.UserId);

                try
                {
                    var handled = await handler.HandleAsync(context, cancellationToken);
                    if (handled)
                    {
                        logger.LogDebug(
                            "Callback handled: prefix={Prefix}, handler={HandlerName}, user={UserId}",
                            context.ParsedCallback.Prefix,
                            handler.GetType().Name,
                            context.UserId);
                        return true;
                    }
                }
                catch (OperationCanceledException)
                {
                    logger.LogInformation(
                        "Callback '{Prefix}' handling was cancelled",
                        context.ParsedCallback.Prefix);
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Error in handler {HandlerName} for callback '{Prefix}'",
                        handler.GetType().Name,
                        context.ParsedCallback.Prefix);
                    // Continue processing - don't rethrow to prevent stopping update handling
                }
            }
        }

        logger.LogDebug(
            "Callback ignored: prefix={Prefix}, user={UserId}, reason=no_handler",
            context.ParsedCallback.Prefix,
            context.UserId);

        return false;
    }
}
