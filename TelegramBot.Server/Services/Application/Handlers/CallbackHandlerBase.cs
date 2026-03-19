using TelegramBot.Core.Interfaces;

namespace TelegramBot.Server.Services.Application.Handlers;

/// <summary>
/// Базовый класс для обработчиков callback-запросов.
/// </summary>
public abstract class CallbackHandlerBase : ICallbackHandler
{
    protected readonly ILogger Logger;

    protected CallbackHandlerBase(ILogger logger)
    {
        Logger = logger;
    }

    protected virtual HashSet<string> SupportedPrefixes { get; } = [];

    public virtual int Priority => 100;

    public virtual bool CanHandle(string prefix)
        => SupportedPrefixes.Count > 0 && SupportedPrefixes.Contains(prefix);

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

    protected abstract Task<bool> HandleAsyncInternal(CallbackContext context, CancellationToken cancellationToken = default);

    protected void LogInvalidInput(string fieldName, object? value, string? username, long userId)
    {
        Logger.LogWarning("Invalid {FieldName} '{Value}' from user {Username} ({UserId})", fieldName, value, username, userId);
    }

    private static string GetPrefix(CallbackContext context) => context.ParsedCallback.Prefix;
}
