using Microsoft.Extensions.Options;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Config;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Application.Handlers;

/// <summary>
/// Обработчик запросов регистрации и управления доступом пользователей.
/// </summary>
public sealed class AccessRequestHandler : CallbackHandlerBase
{
    private readonly IDataService _dataService;
    private readonly ITelegramOutputService _outputService;
    private readonly long[] _adminIds;

    public override int Priority => 0;

    protected override HashSet<string> SupportedPrefixes { get; } =
    [
        CallbackPrefixes.RequestAccess,
        CallbackPrefixes.ApproveUser,
        CallbackPrefixes.RejectUser
    ];

    public AccessRequestHandler(
        IDataService dataService,
        ITelegramOutputService outputService,
        IOptions<BotOptions> botOptions,
        ILogger<AccessRequestHandler> logger) : base(logger)
    {
        _dataService = dataService;
        _outputService = outputService;
        _adminIds = botOptions.Value.AdminUserIds;
    }

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
        var existing = await _dataService.GetUserAsync(context.UserId);

        if (existing?.Status == UserAccessStatus.Approved)
        {
            await _outputService.EditMessageReplyTextAsync(context.UserId, context.MessageId,
                "У вас уже есть доступ. Введите /help для просмотра команд.");
            return true;
        }

        if (existing?.Status == UserAccessStatus.Pending)
        {
            await _outputService.EditMessageReplyTextAsync(context.UserId, context.MessageId,
                "Ваш запрос уже отправлен. Ожидайте подтверждения администратора.");
            return true;
        }

        var now = DateTime.UtcNow;
        await _dataService.UpsertUserAsync(new BotUser
        {
            UserId = context.UserId,
            Username = context.Username,
            Role = UserRole.User,
            Status = UserAccessStatus.Pending,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now
        });

        await _outputService.EditMessageReplyTextAsync(context.UserId, context.MessageId,
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
            await _outputService.SendMessageWithKeyboardAsync(adminId,
                $"Запрос доступа от {displayName} (ID: {context.UserId})",
                keyboard);
        }

        return true;
    }

    private async Task<bool> HandleApproveAsync(CallbackContext context)
    {
        if (!long.TryParse(context.ParsedCallback.Argument, out long targetUserId))
        {
            LogInvalidInput("user ID", context.ParsedCallback.Argument, context.Username, context.UserId);
            return true;
        }

        var user = await _dataService.GetUserAsync(targetUserId);
        if (user == null)
        {
            await _outputService.EditMessageReplyTextAsync(context.UserId, context.MessageId,
                "Пользователь не найден.");
            return true;
        }

        user.Status = UserAccessStatus.Approved;
        user.UpdatedAt = DateTime.UtcNow;
        await _dataService.UpsertUserAsync(user);

        var displayName = string.IsNullOrEmpty(user.Username) ? targetUserId.ToString() : $"@{user.Username}";
        await _outputService.EditMessageReplyTextAsync(context.UserId, context.MessageId,
            $"✅ Пользователь {displayName} одобрен.");

        await _outputService.SendMessageAsync(targetUserId,
            "Доступ предоставлен! Введите /help для просмотра доступных команд.");

        return true;
    }

    private async Task<bool> HandleRejectAsync(CallbackContext context)
    {
        if (!long.TryParse(context.ParsedCallback.Argument, out long targetUserId))
        {
            LogInvalidInput("user ID", context.ParsedCallback.Argument, context.Username, context.UserId);
            return true;
        }

        var user = await _dataService.GetUserAsync(targetUserId);
        if (user == null)
        {
            await _outputService.EditMessageReplyTextAsync(context.UserId, context.MessageId,
                "Пользователь не найден.");
            return true;
        }

        user.Status = UserAccessStatus.Rejected;
        user.UpdatedAt = DateTime.UtcNow;
        await _dataService.UpsertUserAsync(user);

        var displayName = string.IsNullOrEmpty(user.Username) ? targetUserId.ToString() : $"@{user.Username}";
        await _outputService.EditMessageReplyTextAsync(context.UserId, context.MessageId,
            $"❌ Пользователь {displayName} отклонён.");

        await _outputService.SendMessageAsync(targetUserId,
            "Ваш запрос на доступ отклонён. Обратитесь к администратору.");

        return true;
    }
}
