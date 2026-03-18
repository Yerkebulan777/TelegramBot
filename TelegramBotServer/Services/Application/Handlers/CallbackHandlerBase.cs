using TelegramBotServer.Interfaces;

namespace TelegramBotServer.Services.Application.Handlers;

/// <summary>
/// Базовый класс для обработчиков callback-запросов.
/// Предоставляет базовую обработку ошибок и логирование.
/// </summary>
public abstract class CallbackHandlerBase : ICallbackHandler
{
    protected readonly ILogger Logger;

    protected CallbackHandlerBase(ILogger logger)
    {
        Logger = logger;
    }

    /// <summary>
    /// Префиксы, которые может обрабатывать этот обработчик.
    /// </summary>
    protected virtual HashSet<string> SupportedPrefixes { get; } = [];

    /// <inheritdoc/>
    public virtual int Priority => 100;

    /// <inheritdoc/>
    public virtual bool CanHandle(string prefix)
        => SupportedPrefixes.Count > 0 && SupportedPrefixes.Contains(prefix);

    /// <inheritdoc/>
    public async Task<bool> HandleAsync(CallbackContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await HandleAsyncInternal(context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Logger.LogInformation("Callback handling was cancelled for prefix '{Prefix}'", GetPrefix(context));
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error handling callback with prefix '{Prefix}' for user {Username} ({UserId})",
                GetPrefix(context), context.Username, context.UserId);
            throw;
        }
    }

    /// <summary>
    /// Реализация обработки callback в подклассах.
    /// </summary>
    protected abstract Task<bool> HandleAsyncInternal(CallbackContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Логирует предупреждение для некорректных входных данных.
    /// </summary>
    protected void LogInvalidInput(string fieldName, object? value, long userId)
    {
        Logger.LogWarning("Invalid {FieldName} '{Value}' from user {UserId}", fieldName, value, userId);
    }

    private static string GetPrefix(CallbackContext context) => context.ParsedCallback.Prefix;
}
