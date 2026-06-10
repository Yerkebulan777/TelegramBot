using TelegramBot.Core.Constants;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;
using TelegramBot.Server.Interfaces;
using Message = Telegram.Bot.Types.Message;

namespace TelegramBot.Server.Middleware;

public interface IAccessValidator
{
    /// <summary>
    /// Returns current user access flags used by command and callback pipelines.
    /// </summary>
    Task<AccessValidationResult> ValidateAsync(long userId);

    /// <summary>
    /// Returns true when a callback prefix must bypass normal active-user validation.
    /// </summary>
    bool BypassesAccessCheck(string callbackPrefix);

    /// <summary>
    /// Sends and tracks the access-denied message when the user is not active.
    /// </summary>
    Task<bool> EnsureActiveOrNotifyAsync(long userId, UserSession session);

    /// <summary>
    /// Refreshes an approved admin record before /start access handling.
    /// </summary>
    Task<BotUser?> RefreshApprovedAdminUserAsync(long userId, string username, BotUser? user);
}

public sealed class AuthorizationMiddleware(
    IUserDataService userDataService,
    IMessageTrackingDataService messageTrackingDataService,
    ITelegramOutputService outputService) : IAccessValidator
{
    public async Task<AccessValidationResult> ValidateAsync(long userId)
    {
        var user = await userDataService.GetUserAsync(userId);
        var isAdmin = user?.Role == UserRole.Admin && user.Status == UserAccessStatus.Approved;
        var isBanned = user?.Status == UserAccessStatus.Blocked;
        var isActive = user?.Status == UserAccessStatus.Approved && !isBanned;

        return new AccessValidationResult(user, isAdmin, isBanned, isActive);
    }

    public bool BypassesAccessCheck(string callbackPrefix)
    {
        return callbackPrefix is
            CallbackPrefixes.RequestAccess or
            CallbackPrefixes.ApproveUser or
            CallbackPrefixes.RejectUser;
    }

    public async Task<bool> EnsureActiveOrNotifyAsync(long userId, UserSession session)
    {
        var access = await ValidateAsync(userId);
        if (access.IsActive)
        {
            return true;
        }

        _ = await TrackMessageAsync(
            outputService.SendMessageAsync(userId, "У вас нет доступа. Введите /start для запроса доступа."),
            session);

        return false;
    }

    public async Task<BotUser?> RefreshApprovedAdminUserAsync(long userId, string username, BotUser? user)
    {
        var adminUser = await userDataService.GetUserAsync(userId);
        if (adminUser?.Role != UserRole.Admin || adminUser.Status != UserAccessStatus.Approved)
        {
            return user;
        }

        var now = DateTime.UtcNow;
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

    private async Task<Message?> TrackMessageAsync(Task<Message?> task, UserSession session)
    {
#pragma warning disable VSTHRD003 // Foreign Task passed as parameter — intentionally awaited here
        var msg = await task;
#pragma warning restore VSTHRD003
        if (msg != null)
        {
            var sessionId = session.SessionId > 0 ? session.SessionId : (int?)null;
            await messageTrackingDataService.TrackMessageAsync(msg.Chat.Id, msg.MessageId, sessionId);
        }

        return msg;
    }
}

public sealed record AccessValidationResult(
    BotUser? User,
    bool IsAdmin,
    bool IsBanned,
    bool IsActive)
{
    public bool HasAccess => IsActive;
}
