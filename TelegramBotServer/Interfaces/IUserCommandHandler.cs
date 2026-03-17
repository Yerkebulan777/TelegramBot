using TelegramBotServer.DTOs;
using TelegramBotServer.Models;

namespace TelegramBotServer.Interfaces;

/// <summary>
/// Defines a handler for processing user text commands (e.g. /start, /help).
/// </summary>
public interface IUserCommandHandler
{
    /// <summary>
    /// The command trigger, e.g. "/start" (must be lowercase).
    /// </summary>
    string Command { get; }

    /// <summary>
    /// Handles the command.
    /// </summary>
    /// <param name="message">The message DTO containing user text.</param>
    /// <param name="session">The user's current session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task HandleAsync(MessageDto message, UserSession session, CancellationToken cancellationToken = default);
}
