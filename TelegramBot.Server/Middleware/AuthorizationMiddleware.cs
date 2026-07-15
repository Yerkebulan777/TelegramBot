using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Data;

namespace TelegramBot.Server.Middleware;

public sealed class AuthorizationMiddleware(
    UserDataService userDataService,
    ILogger<AuthorizationMiddleware> logger)
{
    public async Task<AccessValidationResult> ValidateAsync(long userId)
    {
        var user = await userDataService.GetUserAsync(userId);
        var isAdmin = user?.Role == UserRole.Admin && user.Status == UserAccessStatus.Approved;
        var isActive = user?.Status == UserAccessStatus.Approved;

        return new AccessValidationResult(user, isAdmin, isActive);
    }

    public bool BypassesAccessCheck(string callbackPrefix)
    {
        return callbackPrefix is
            CallbackPrefixes.RequestAccess or
            CallbackPrefixes.ApproveUser or
            CallbackPrefixes.RejectUser or
            CallbackPrefixes.BlockUser;
    }

    /// <summary>
    /// Безопасно обновляет данные администратора с защитой от race condition.
    /// Использует optimistic concurrency через UpdatedAt timestamp.
    /// </summary>
    public async Task<BotUser?> RefreshApprovedAdminUserAsync(long userId, string username, BotUser? user)
    {
        var adminUser = await userDataService.GetUserAsync(userId);
        if (adminUser?.Role != UserRole.Admin || adminUser.Status != UserAccessStatus.Approved)
        {
            return user;
        }

        var now = DateTime.UtcNow;

        // Проверка на race condition: если пользователь был изменен между чтением и записью
        if (user != null && adminUser.UpdatedAt > user.UpdatedAt)
        {
            logger.LogDebug("Skip update admin {UserId}: concurrent modification", userId);
            return adminUser;
        }

        await userDataService.UpsertUserAsync(new BotUser
        {
            UserId = userId,
            Username = username,
            Role = UserRole.Admin,
            Status = UserAccessStatus.Approved,
            CreatedAt = user?.CreatedAt ?? now,
            UpdatedAt = now
        });

        return await userDataService.GetUserAsync(userId);
    }

}

public sealed record AccessValidationResult(
    BotUser? User,
    bool IsAdmin,
    bool IsActive);
