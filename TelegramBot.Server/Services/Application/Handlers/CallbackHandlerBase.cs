using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;
using TelegramBot.Server.Constants;

namespace TelegramBot.Server.Services.Application.Handlers;

public abstract class CallbackHandlerBase(ILogger logger) : ICallbackHandler
{
    protected readonly ILogger Logger = logger;

    protected virtual HashSet<string> SupportedPrefixes { get; } = [];

    public virtual int Priority => HandlerPriorities.Default;

    public virtual bool CanHandle(string prefix)
    {
        return SupportedPrefixes.Count > 0 && SupportedPrefixes.Contains(prefix);
    }

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

    private static string GetPrefix(CallbackContext context)
    {
        return context.ParsedCallback.Prefix;
    }
}
