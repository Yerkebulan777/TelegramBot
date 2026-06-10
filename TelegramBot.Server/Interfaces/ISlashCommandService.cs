
using TelegramBot.Core.DTOs;
using TelegramBot.Core.Models;

namespace TelegramBot.Server.Interfaces;

public interface ISlashCommandService
{
    Task HandleUserCommandAsync(MessageDto message, UserSession session, CancellationToken cancellationToken = default);
}
