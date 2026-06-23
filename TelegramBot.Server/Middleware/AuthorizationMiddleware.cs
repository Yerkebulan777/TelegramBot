using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Data;
using TelegramBot.Server.Interfaces;
using Message = Telegram.Bot.Types.Message;

namespace TelegramBot.Server.Middleware;

public sealed class AuthorizationMiddleware(
    UserDataService userDataService,
    MessageTrackingDataService messageTrackingDataService,
    ITelegramOutputService outputService,
    ILogger<AuthorizationMiddleware> logger)
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
            logger.LogDebug("Skipping update for admin {UserId}: concurrent modification detected", userId);
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
    bool IsActive);
