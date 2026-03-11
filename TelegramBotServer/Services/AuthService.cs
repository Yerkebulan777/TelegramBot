using TelegramBotServer.Interfaces;

namespace TelegramBotServer.Services
{
    public class AuthService(IDataService dataService) : IAuthService
    {
        private readonly IDataService _dataService = dataService;

        public async Task<bool> CheckAuthAsync(long userId)
        {
            var authorized = await _dataService.IsUserAuthorizedAsync(userId);

            if (authorized)
            {
                return true;
            }

            return false;
        }

        public async Task<bool> AuthorizeUserAsync(long userId, string username, string password)
        {
            var validatePassword = await _dataService.ValidatePasswordAsync(password);

            if (validatePassword)
            {
                await _dataService.AddAuthorizedUserAsync(userId, username);
                return true;
            }

            return false;
        }
    }
}
