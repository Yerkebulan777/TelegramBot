#nullable enable

using TelegramBot.Core.DTOs;
using TelegramBot.Core.Models;

namespace TelegramBot.Server.Interfaces;

public interface ISlashCommandService
{
    Task HandleUserCommandAsync(MessageDto message, UserSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns false and sends an "access denied" message if the user is not approved.
    /// </summary>
    Task<bool> CheckAndNotifyAccessAsync(long userId);
}
