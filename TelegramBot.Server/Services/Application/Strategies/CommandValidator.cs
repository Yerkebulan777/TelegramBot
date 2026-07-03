using TelegramBot.Core.DTOs;
using TelegramBot.Core.Models;
using TelegramBot.Server.Middleware;

namespace TelegramBot.Server.Services.Application.Strategies;

/// <summary>
/// Валидация пользовательского контекста для команд.
/// </summary>
public sealed class CommandValidator(AuthorizationMiddleware accessValidator, ILogger<CommandValidator> logger)
{
    /// <summary>
    /// Валидирует контекст пользователя и возвращает готовый UserCommandContext.
    /// </summary>
    public async Task<UserCommandContext> ValidateAsync(
        MessageDto message,
        UserSession session,
        long userId,
        string command,
        CancellationToken cancellationToken = default)
    {
        var username = message.Username;
        var text = NormalizeCommandText(command);

        ArgumentNullException.ThrowIfNullOrWhiteSpace(username);

        logger.LogDebug("Command received: command={Command}, user={Username} ({UserId})", text, username, userId);

        var access = await accessValidator.ValidateAsync(userId);
        var user = access.User;

        if (text == "/start" && !access.IsActive)
        {
            _ = await accessValidator.RefreshApprovedAdminUserAsync(userId, username, user);
            access = await accessValidator.ValidateAsync(userId);
            user = access.User;
        }

        return new UserCommandContext(
            message,
            session,
            userId,
            message.ChatId == 0 ? userId : message.ChatId,
            command,
            text,
            username,
            user,
            text == "/start" || access.IsActive);
    }

    private static string NormalizeCommandText(string text)
    {
        if (!text.StartsWith('/'))
        {
            return text;
        }

        var mentionIndex = text.IndexOf('@');
        if (mentionIndex > 0)
        {
            text = text[..mentionIndex];
        }

        return text.ToLowerInvariant();
    }
}
