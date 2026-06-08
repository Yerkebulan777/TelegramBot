using Microsoft.Extensions.Options;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Config;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;
using TelegramBot.Server.Constants;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class AccessRequestHandler(
    IDataService dataService,
    ITelegramOutputService outputService,
    IOptions<BotOptions> botOptions,
    ILogger<AccessRequestHandler> logger) : CallbackHandlerBase(logger)
{
    private readonly long[] _adminIds = botOptions.Value.AdminUserIds;

    public override int Priority => HandlerPriorities.AccessRequest;

    protected override HashSet<string> SupportedPrefixes { get; } =
    [
        CallbackPrefixes.RequestAccess,
        CallbackPrefixes.ApproveUser,
        CallbackPrefixes.RejectUser
    ];

    protected override async Task<bool> HandleAsyncInternal(CallbackContext context, CancellationToken cancellationToken = default)
    {
        return context.ParsedCallback.Prefix switch
        {
            CallbackPrefixes.RequestAccess => await HandleRequestAccessAsync(context),
            CallbackPrefixes.ApproveUser   => await HandleApproveAsync(context),
            CallbackPrefixes.RejectUser    => await HandleRejectAsync(context),
            _ => false
        };
    }

    private async Task<bool> HandleRequestAccessAsync(CallbackContext context)
    {
        var existing = await dataService.GetUserAsync(context.UserId);

        if (existing?.Status == UserAccessStatus.Approved)
        {
            await outputService.EditMessageReplyTextAsync(context.UserId, context.MessageId,
                "У вас уже есть доступ. Введите /help для просмотра команд.");
            return true;
        }

        if (existing?.Status == UserAccessStatus.Pending)
        {
            await outputService.EditMessageReplyTextAsync(context.UserId, context.MessageId,
                "Ваш запрос уже отправлен. Ожидайте подтверждения администратора.");
            return true;
        }

        var now = DateTime.UtcNow;
        await dataService.UpsertUserAsync(new BotUser
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
            InlineKeyboardButton.WithCallbackData("❌ Отклонить", $"{CallbackPrefixes.RejectUser}{context.UserId}")
        ]]);

        var displayName = string.IsNullOrEmpty(context.Username)
            ? context.UserId.ToString()
            : $"@{context.Username}";

        foreach (var adminId in _adminIds)
        {
            await outputService.SendMessageWithKeyboardAsync(adminId,
                $"Запрос доступа от {displayName} (ID: {context.UserId})",
                keyboard);
        }

        return true;
    }

    private Task<bool> HandleApproveAsync(CallbackContext context)
        => HandleAccessDecisionAsync(context,
            UserAccessStatus.Approved,
            displayName => $"✅ Пользователь {displayName} одобрен.",
            "Доступ предоставлен! Введите /help для просмотра доступных команд.");

    private Task<bool> HandleRejectAsync(CallbackContext context)
        => HandleAccessDecisionAsync(context,
            UserAccessStatus.Rejected,
            displayName => $"❌ Пользователь {displayName} отклонён.",
            "Ваш запрос на доступ отклонён. Обратитесь к администратору.");

    private async Task<bool> HandleAccessDecisionAsync(
        CallbackContext context,
        UserAccessStatus newStatus,
        Func<string, string> adminMessage,
        string userMessage)
    {
        if (!long.TryParse(context.ParsedCallback.Argument, out long targetUserId))
        {
            LogInvalidInput("user ID", context.ParsedCallback.Argument, context.Username, context.UserId);
            return true;
        }

        var user = await dataService.GetUserAsync(targetUserId);
        if (user == null)
        {
            await outputService.EditMessageReplyTextAsync(context.UserId, context.MessageId, "Пользователь не найден.");
            return true;
        }

        user.Status = newStatus;
        user.UpdatedAt = DateTime.UtcNow;
        await dataService.UpsertUserAsync(user);

        var displayName = string.IsNullOrEmpty(user.Username) ? targetUserId.ToString() : $"@{user.Username}";
        await outputService.EditMessageReplyTextAsync(context.UserId, context.MessageId, adminMessage(displayName));
        await outputService.SendMessageAsync(targetUserId, userMessage);

        return true;
    }
}
