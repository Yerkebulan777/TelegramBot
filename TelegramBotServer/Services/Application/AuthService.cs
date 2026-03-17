using TelegramBotServer.Interfaces;

namespace TelegramBotServer.Services.Application;

/// <summary>
/// Сервис аутентификации пользователей.
/// </summary>
public class AuthService(IDataService dataService) : IAuthService
{
    private readonly IDataService _dataService = dataService;

    /// <inheritdoc/>
    public Task<bool> CheckAuthAsync(long userId)
    {
        return _dataService.IsUserAuthorizedAsync(userId);
    }

    /// <inheritdoc/>
    public async Task<bool> AuthorizeUserAsync(long userId, string username, string password)
    {
        if (!await _dataService.ValidatePasswordAsync(password))
        {
            return false;
        }

        await _dataService.AddAuthorizedUserAsync(userId, username);

        return true;
    }
}
