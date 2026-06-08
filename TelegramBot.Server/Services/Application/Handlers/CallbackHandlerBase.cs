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

    public Task<bool> HandleAsync(CallbackContext context, CancellationToken cancellationToken = default)
    {
        return HandleAsyncInternal(context, cancellationToken);
    }

    protected abstract Task<bool> HandleAsyncInternal(CallbackContext context, CancellationToken cancellationToken = default);

    protected void LogInvalidInput(string fieldName, object? value, string? username, long userId)
    {
        Logger.LogWarning("Invalid {FieldName} '{Value}' from user {Username} ({UserId})", fieldName, value, username, userId);
    }


}
