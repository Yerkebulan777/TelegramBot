using TelegramBotServer.Interfaces;

namespace TelegramBotServer.Services.Application.Handlers;

/// <summary>
/// Base class for callback handlers providing common functionality.
/// </summary>
public abstract class CallbackHandlerBase : ICallbackHandler
{
    protected readonly ILogger Logger;

    protected CallbackHandlerBase(ILogger logger)
    {
        Logger = logger;
    }

    /// <summary>
    /// Override to specify which prefixes this handler can process.
    /// </summary>
    protected virtual HashSet<string> SupportedPrefixes { get; } = [];

    /// <inheritdoc/>
    public virtual int Priority => 100;

    /// <inheritdoc/>
    public virtual bool CanHandle(string prefix)
        => SupportedPrefixes.Count > 0 && SupportedPrefixes.Contains(prefix);

    /// <inheritdoc/>
    public abstract Task<bool> HandleAsync(CallbackContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Logs a warning for invalid input.
    /// </summary>
    protected void LogInvalidInput(string fieldName, object? value, long userId)
    {
        Logger.LogWarning("Invalid {FieldName} '{Value}' from user {UserId}", fieldName, value, userId);
    }
}
