using Microsoft.Extensions.Options;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Data;
using TelegramBot.Server.Middleware;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class AccessRequestHandler(
    UserDataService userDataService,
    TelegramOutputService outputService,
    AuthorizationMiddleware accessValidator,
    IOptions<BotOptions> botOptions,
    ILogger<AccessRequestHandler> logger) : CallbackHandlerBase(logger)
{
    private readonly long _adminId = botOptions.Value.AdminUserId;

    protected override HashSet<string> SupportedPrefixes { get; } =
    [
        CallbackPrefixes.RequestAccess,
        CallbackPrefixes.ApproveUser,
        CallbackPrefixes.RejectUser,
        CallbackPrefixes.BlockUser
    ];

    public override Task HandleAsync(CallbackContext context, CancellationToken cancellationToken = default)
    {
        return context.ParsedCallback.Prefix switch
        {
            CallbackPrefixes.RequestAccess => HandleRequestAccessAsync(context),
            CallbackPrefixes.ApproveUser => HandleApproveAsync(context),
            CallbackPrefixes.RejectUser => HandleRejectAsync(context),
            CallbackPrefixes.BlockUser => HandleBlockAsync(context),
            _ => Task.CompletedTask
        };
    }

    private async Task HandleRequestAccessAsync(CallbackContext context)
    {
        var access = await accessValidator.ValidateAsync(context.UserId);
        var existing = access.User;

        if (access.IsActive)
        {
            await outputService.EditMessageReplyTextAsync(context.UserId, context.MessageId,
                "У вас уже есть доступ. Введите /help для просмотра команд.");
            return;
        }

        if (existing?.Status == UserAccessStatus.Pending)
        {
            await outputService.EditMessageReplyTextAsync(context.UserId, context.MessageId,
                "Ваш запрос уже отправлен. Ожидайте подтверждения администратора.");
            return;
        }

        if (existing?.Status == UserAccessStatus.Blocked)
        {
            await outputService.EditMessageReplyTextAsync(context.UserId, context.MessageId,
                "Вы заблокированы. Обратитесь к администратору.");
            return;
        }

        var now = DateTime.UtcNow;
        await userDataService.UpsertUserAsync(new BotUser
        {
            UserId = context.UserId,
            Username = context.Username,
            Role = UserRole.User,
            Status = UserAccessStatus.Pending,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now
        });

        await outputService.EditMessageReplyTextAsync(context.UserId, context.MessageId,
            "Ваш запрос отправлен. Ожидайте подтверждения администратора.");

        var keyboard = new InlineKeyboardMarkup([[
            InlineKeyboardButton.WithCallbackData("✅ Одобрить", $"{CallbackPrefixes.ApproveUser}{context.UserId}"),
            InlineKeyboardButton.WithCallbackData("❌ Отклонить", $"{CallbackPrefixes.RejectUser}{context.UserId}"),
            InlineKeyboardButton.WithCallbackData("🚫 Заблокировать", $"{CallbackPrefixes.BlockUser}{context.UserId}")
        ]]);

        var displayName = string.IsNullOrEmpty(context.Username)
            ? context.UserId.ToString()
            : $"@{context.Username}";

        if (_adminId != 0)
        {
            _=await outputService.SendMessageWithKeyboardAsync(_adminId,
                $"Запрос доступа от {displayName} (ID: {context.UserId})",
                keyboard);
        }

    }

    private Task HandleApproveAsync(CallbackContext context)
    {
        return HandleAccessDecisionAsync(context,
                UserAccessStatus.Approved,
                displayName => $"✅ Пользователь {displayName} одобрен.",
                "Доступ предоставлен! Введите /help для просмотра доступных команд.");
    }

    private Task HandleRejectAsync(CallbackContext context)
    {
        return HandleAccessDecisionAsync(context,
                UserAccessStatus.Rejected,
                displayName => $"❌ Пользователь {displayName} отклонён.",
                "Ваш запрос на доступ отклонён. Обратитесь к администратору.");
    }

    private Task HandleBlockAsync(CallbackContext context)
    {
        return HandleAccessDecisionAsync(context,
                UserAccessStatus.Blocked,
                displayName => $"🚫 Пользователь {displayName} заблокирован.",
                "Вы заблокированы администратором. Обратитесь к администратору.");
    }

    private async Task HandleAccessDecisionAsync(
        CallbackContext context,
        UserAccessStatus newStatus,
        Func<string, string> adminMessage,
        string userMessage)
    {
        var access = await accessValidator.ValidateAsync(context.UserId);
        if (!access.IsAdmin)
        {
            Logger.LogWarning("Access decision rejected: user={Username} ({UserId}), reason=not_admin", context.Username, context.UserId);
            await outputService.EditMessageReplyTextAsync(context.UserId, context.MessageId, "Недостаточно прав.");
            return;
        }

        if (!long.TryParse(context.ParsedCallback.Argument, out var targetUserId))
        {
            LogInvalidInput("user ID", context.ParsedCallback.Argument, context.Username, context.UserId);
            return;
        }

        var user = await userDataService.GetUserAsync(targetUserId);
        if (user == null)
        {
            await outputService.EditMessageReplyTextAsync(context.UserId, context.MessageId, "Пользователь не найден.");
            return;
        }

        user.Status = newStatus;
        user.UpdatedAt = DateTime.UtcNow;
        await userDataService.UpsertUserAsync(user);

        var displayName = string.IsNullOrEmpty(user.Username) ? targetUserId.ToString() : $"@{user.Username}";
        await outputService.EditMessageReplyTextAsync(context.UserId, context.MessageId, adminMessage(displayName));
        _=await outputService.SendMessageAsync(targetUserId, userMessage);

    }
}
