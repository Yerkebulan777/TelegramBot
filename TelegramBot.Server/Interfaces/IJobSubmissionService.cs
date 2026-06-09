using TelegramBot.Core.Models;

namespace TelegramBot.Server.Interfaces;

public interface IJobSubmissionService
{
    Task<bool> SubmitJobAsync(long userId, string username, UserSession session, CancellationToken cancellationToken);
}
