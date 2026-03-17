using TelegramBotServer.DTOs;
using TelegramBotServer.Models;

namespace TelegramBotServer.Interfaces;

/// <summary>
/// Defines a handler for processing Telegram callback queries.
/// Implements Chain of Responsibility pattern for flexible callback routing.
/// </summary>
public interface ICallbackHandler
{
    /// <summary>
    /// Determines whether this handler can process the given callback.
    /// </summary>
    /// <param name="prefix">The callback prefix (e.g., "PDF:", "OPENFOLDER:").</param>
    /// <returns>True if this handler can process the callback.</returns>
    bool CanHandle(string prefix);

    /// <summary>
    /// Gets the priority order for this handler. Lower values are evaluated first.
    /// Default is 100. Use lower values for more specific handlers.
    /// </summary>
    int Priority => 100;

    /// <summary>
    /// Handles the callback asynchronously.
    /// </summary>
    /// <param name="context">The callback context containing all necessary data.</param>
    /// <param name="cancellationToken">Token for cancellation.</param>
    /// <returns>True if the callback was handled and processing should stop.</returns>
    Task<bool> HandleAsync(CallbackContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Context object containing all data needed for callback processing.
/// </summary>
public sealed class CallbackContext
{
    public required long UserId { get; init; }
    public required long ChatId { get; init; }
    public required int MessageId { get; init; }
    public required string Username { get; init; }
    public required string CallbackQueryId { get; init; }
    public required ParsedCallback ParsedCallback { get; init; }
    public required UserSession Session { get; init; }
    public List<List<ButtonDto>>? Buttons { get; init; }
}
