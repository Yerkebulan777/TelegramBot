using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;

namespace TelegramBot.Server.Services.Application;

/// <summary>
/// Диспетчер callback-запросов с кэшированным маппингом prefix → handler.
/// Оптимизация: O(1) поиск вместо линейного перебора всех хендлеров.
/// </summary>
public sealed class CallbackDispatcher(IEnumerable<ICallbackHandler> handlers, ILogger<CallbackDispatcher> logger)
{
    // Кэш префикс → handler для быстрого поиска (O(1) вместо O(n))
    private readonly Dictionary<string, ICallbackHandler> _handlerMap = 
        handlers
            .SelectMany(h => GetSupportedPrefixes(h).Select(p => (p, h)))
            .GroupBy(x => x.p)
            .ToDictionary(
                g => g.Key, 
                g => g.OrderBy(x => x.h.Priority).First().h);

    /// <summary>
    /// Получает список поддерживаемых префиксов для хендлера.
    /// Использует reflection-safe вызов CanHandle по всем известным callback-префиксам.
    /// </summary>
    private static IEnumerable<string> GetSupportedPrefixes(ICallbackHandler handler)
    {
        foreach (var prefix in GetKnownCallbackPrefixes())
        {
            bool canHandle;
            try
            {
                canHandle = handler.CanHandle(prefix);
            }
            catch
            {
                // Игнорируем ошибки при проверке
                continue;
            }

            if (canHandle)
                yield return prefix;
        }
    }

    private static IEnumerable<string> GetKnownCallbackPrefixes()
    {
        return typeof(CallbackPrefixes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral && !field.IsInitOnly && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);
    }

    /// <summary>
    /// Отправляет callback соответствующему хендлеру.
    /// Логирование улучшено: добавлены детали о найденном хендлере и времени выполнения.
    /// </summary>
    public async Task<bool> DispatchAsync(CallbackContext context, CancellationToken cancellationToken = default)
    {
        var prefix = context.ParsedCallback.Prefix;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        if (!_handlerMap.TryGetValue(prefix, out var handler))
        {
            logger.LogDebug(
                "Callback ignored: prefix={Prefix}, user={UserId}, reason=no_handler",
                prefix,
                context.UserId);
            return false;
        }

        logger.LogDebug(
            "Callback dispatch: prefix={Prefix}, handler={HandlerName}, user={UserId}",
            prefix,
            handler.GetType().Name,
            context.UserId);

        try
        {
            var handled = await handler.HandleAsync(context, cancellationToken);
            stopwatch.Stop();
            
            if (handled)
            {
                logger.LogDebug(
                    "Callback handled: prefix={Prefix}, handler={HandlerName}, user={UserId}, elapsedMs={ElapsedMs}",
                    prefix,
                    handler.GetType().Name,
                    context.UserId,
                    stopwatch.ElapsedMilliseconds);
                return true;
            }
            
            logger.LogDebug(
                "Callback not handled by handler: prefix={Prefix}, handler={HandlerName}, user={UserId}, elapsedMs={ElapsedMs}",
                prefix,
                handler.GetType().Name,
                context.UserId,
                stopwatch.ElapsedMilliseconds);
            return false;
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation(
                "Callback '{Prefix}' handling was cancelled after {ElapsedMs}ms",
                prefix,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Error in handler {HandlerName} for callback '{Prefix}' after {ElapsedMs}ms",
                handler.GetType().Name,
                prefix,
                stopwatch.ElapsedMilliseconds);
            // Continue processing - don't rethrow to prevent stopping update handling
            return false;
        }
    }
}
