namespace TelegramBotServer.Interfaces
{
    public interface IAuthService
    {
        Task<bool> CheckAuthAsync(long userId);
        Task<bool> AuthorizeUserAsync(long userId, string username, string password);
    }
}
