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

    public virtual IEnumerable<string> GetSupportedPrefixes() => SupportedPrefixes;

    public Task<bool> HandleAsync(CallbackContext context, CancellationToken cancellationToken = default)
    {
        return HandleAsyncInternalAsync(context, cancellationToken);
    }

    protected abstract Task<bool> HandleAsyncInternalAsync(CallbackContext context, CancellationToken cancellationToken = default);

    protected void LogInvalidInput(string fieldName, object? value, string? username, long userId)
    {
        Logger.LogWarning("Invalid {FieldName} '{Value}' from user {Username} ({UserId})", fieldName, value, username, userId);
    }

    /// <summary>
    /// Пытается распарсить положительный int из callback-аргумента.
    /// При неудаче логирует через LogInvalidInput и возвращает false.
    /// </summary>
    protected bool TryParseId(CallbackContext context, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out int id)
    {
        if (!int.TryParse(context.ParsedCallback.Argument, out id) || id <= 0)
        {
            LogInvalidInput("ID", context.ParsedCallback.Argument, context.Username, context.UserId);
            return false;
        }
        return true;
    }
}
