using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;
using TelegramBot.Server.Helpers;
using TelegramBot.Server.Interfaces;
using TelegramBot.Server.Middleware;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class SessionManagementHandler(
    ISessionDataService sessionDataService,
    ICommandDataService commandDataService,
    IMessageTrackingDataService messageTrackingDataService,
    IKeyboardBuilder keyboardBuilder,
    ITelegramOutputService outputService,
    IAccessValidator accessValidator,
    ILogger<SessionManagementHandler> logger) : CallbackHandlerBase(logger)
{
    protected override HashSet<string> SupportedPrefixes { get; } =
    [
        CallbackPrefixes.SessionDetails,
        CallbackPrefixes.DeleteSession,
        CallbackPrefixes.DeleteCommand,
        CallbackPrefixes.ConfirmDeleteSession,
        CallbackPrefixes.ConfirmDeleteCommand,
        CallbackPrefixes.DeleteSessionByType,
        CallbackPrefixes.ConfirmDeleteSessionByType
    ];

    /// <summary>Проверяет, имеет ли пользователь доступ (все одобренные могут управлять любыми сессиями).</summary>
    private async Task<bool> CanManageAsync(long userId)
    {
        var access = await accessValidator.ValidateAsync(userId);
        return access.IsActive;
    }

    protected override async Task<bool> HandleAsyncInternalAsync(CallbackContext context, CancellationToken cancellationToken = default)
    {
        return context.ParsedCallback.Prefix switch
        {
            CallbackPrefixes.SessionDetails => await HandleSessionDetailsAsync(context, cancellationToken),
            CallbackPrefixes.DeleteSession => await HandleDeleteSessionConfirmationAsync(context, cancellationToken),
            CallbackPrefixes.DeleteCommand => await HandleDeleteCommandConfirmationAsync(context, cancellationToken),
            CallbackPrefixes.ConfirmDeleteSession => await HandleDeleteSessionAsync(context, cancellationToken),
            CallbackPrefixes.ConfirmDeleteCommand => await HandleDeleteCommandAsync(context, cancellationToken),
            CallbackPrefixes.DeleteSessionByType => await HandleDeleteByTypeConfirmationAsync(context, cancellationToken),
            CallbackPrefixes.ConfirmDeleteSessionByType => await HandleDeleteByTypeAsync(context, cancellationToken),
            _ => false
        };
    }

    private async Task<bool> HandleSessionDetailsAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var arg = context.ParsedCallback.Argument;
        var parts = arg.Split(':');
        if (!int.TryParse(parts[0], out var sessionId) || sessionId <= 0)
        {
            LogInvalidInput("ID", arg, context.Username, context.UserId);
            return true;
        }

        var filter = parts.Length > 1 ? parts[1] : "ALL";
        var session = context.Session;

        if (string.Equals(filter, "SUMMARY", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogInformation("{Username} view session summary {SessionId}", context.Username, sessionId);
            session.IsInStatusView = false;
            session.SessionId = sessionId;

            var sessionStatus = await sessionDataService.GetSessionsStatusAsync(sessionId);
            var keyboard = await keyboardBuilder.GetSessionStatusKeyboardAsync(sessionStatus, sessionId);
            await outputService.EditMessageTextWithKeyboardAsync(context.UserId, context.MessageId, BuildStatusReply(sessionStatus), keyboard);
            session.StatusMessageId = context.MessageId;
        }
        else
        {
            Logger.LogInformation("{Username} view cmds {SessionId} with filter {Filter}", context.Username, sessionId, filter);
            session.IsInStatusView = true;
            session.SessionId = sessionId;

            var sessionStatus = await sessionDataService.GetSessionsStatusAsync(sessionId);
            var sessionCommands = await sessionDataService.GetSessionsCommandsAsync(sessionId);

            var keyboard = await keyboardBuilder.GetSessionCommandsKeyboardAsync(sessionCommands, sessionId, filter);
            await outputService.EditMessageTextWithKeyboardAsync(context.UserId, context.MessageId, BuildStatusReply(sessionStatus, sessionCommands), keyboard);
            session.StatusMessageId = context.MessageId;
        }

        return true;
    }

    private async Task<bool> HandleDeleteSessionConfirmationAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        if (!TryParseId(context, out var sessionId))
        {
            return true;
        }

        Logger.LogInformation("{Username} requested delete confirmation for session {SessionId}", context.Username, sessionId);
        context.Session.IsInStatusView = true;

        var keyboard = new InlineKeyboardMarkup(
        [
            [
                InlineKeyboardButton.WithCallbackData("✅ Да, удалить", $"{CallbackPrefixes.ConfirmDeleteSession}{sessionId}"),
                InlineKeyboardButton.WithCallbackData("↩️ Назад", $"{CallbackPrefixes.SessionDetails}{sessionId}")
            ]
        ]);

        await outputService.EditMessageTextWithKeyboardAsync(
            context.UserId,
            context.MessageId,
            $"Удалить сессию #{sessionId} и все её команды?",
            keyboard);

        return true;
    }

    private async Task<bool> HandleDeleteSessionAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        if (!TryParseId(context, out var sessionId))
        {
            return true;
        }

        Logger.LogInformation("{Username} delete session {SessionId}", context.Username, sessionId);

        var isAdmin = await CanManageAsync(context.UserId);
        if (!await sessionDataService.DeleteSessionAsync(sessionId, context.UserId, isAdmin))
        {
            return true;
        }

        // Очищаем tracked messages из БД
        await messageTrackingDataService.DeleteTrackedMessagesBySessionAsync(sessionId);

        context.Session.IsInStatusView = true;
        await ShowSessionsListAsync(context);

        return true;
    }

    private async Task<bool> HandleDeleteCommandConfirmationAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var arg = context.ParsedCallback.Argument;
        var parts = arg.Split(':');
        if (!int.TryParse(parts[0], out var commandId) || commandId <= 0)
        {
            LogInvalidInput("ID", arg, context.Username, context.UserId);
            return true;
        }
        var filter = parts.Length > 1 ? parts[1] : "ALL";

        var isAdmin = await CanManageAsync(context.UserId);
        var sessionId = await sessionDataService.GetSessionIdByCommandAsync(commandId, context.UserId, isAdmin);
        if (!sessionId.HasValue)
        {
            Logger.LogWarning("{Username} foreign cmd {CommandId}", context.Username, commandId);
            return true;
        }

        Logger.LogInformation("{Username} requested delete confirmation for command {CommandId}", context.Username, commandId);
        context.Session.IsInStatusView = false;

        var keyboard = new InlineKeyboardMarkup(
        [
            [
                InlineKeyboardButton.WithCallbackData("✅ Да, удалить", $"{CallbackPrefixes.ConfirmDeleteCommand}{commandId}:{filter}"),
                InlineKeyboardButton.WithCallbackData("↩️ Назад", $"{CallbackPrefixes.SessionDetails}{sessionId.Value}:{filter}")
            ]
        ]);

        await outputService.EditMessageTextWithKeyboardAsync(
            context.UserId,
            context.MessageId,
            $"Удалить команду #{commandId}?",
            keyboard);

        return true;
    }

    private async Task<bool> HandleDeleteCommandAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var arg = context.ParsedCallback.Argument;
        var parts = arg.Split(':');
        if (!int.TryParse(parts[0], out var commandId) || commandId <= 0)
        {
            LogInvalidInput("ID", arg, context.Username, context.UserId);
            return true;
        }
        var filter = parts.Length > 1 ? parts[1] : "ALL";

        Logger.LogInformation("{Username} delete cmd {CommandId}", context.Username, commandId);

        var isAdmin = await CanManageAsync(context.UserId);
        var sessionId = await sessionDataService.GetSessionIdByCommandAsync(commandId, context.UserId, isAdmin);
        if (!sessionId.HasValue)
        {
            Logger.LogWarning("{Username} foreign cmd {CommandId}", context.Username, commandId);
            return true;
        }

        if (!await commandDataService.DeleteCommandAsync(commandId, context.UserId, isAdmin))
        {
            return true;
        }

        context.Session.SessionId = sessionId.Value;

        if (!await sessionDataService.CheckCommandsStatusAsync(sessionId.Value))
        {
            // Последняя команда — удаляем сессию и tracked messages
            if (await sessionDataService.DeleteSessionAsync(sessionId.Value, context.UserId, isAdmin))
            {
                await messageTrackingDataService.DeleteTrackedMessagesBySessionAsync(sessionId.Value);
                context.Session.IsInStatusView = true;
                await ShowSessionsListAsync(context);
            }
        }
        else
        {
            var sessionStatus = await sessionDataService.GetSessionsStatusAsync(sessionId.Value);
            var sessionCommands = await sessionDataService.GetSessionsCommandsAsync(sessionId.Value);

            var newKeyboard = await keyboardBuilder.GetSessionCommandsKeyboardAsync(sessionCommands, sessionId.Value, filter);
            await outputService.EditMessageTextWithKeyboardAsync(context.UserId, context.MessageId, BuildStatusReply(sessionStatus, sessionCommands), newKeyboard);
        }

        return true;
    }

    private async Task<bool> HandleDeleteByTypeConfirmationAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var arg = context.ParsedCallback.Argument;
        var parts = arg.Split(':');
        if (!int.TryParse(parts[0], out var sessionId) || sessionId <= 0 || parts.Length < 2)
        {
            LogInvalidInput("ID:Type", arg, context.Username, context.UserId);
            return true;
        }

        var commandType = parts[1];
        Logger.LogInformation("{Username} requested delete confirmation for type {CommandType} in session {SessionId}", context.Username, commandType, sessionId);

        var keyboard = new InlineKeyboardMarkup(
        [
            [
                InlineKeyboardButton.WithCallbackData("✅ Да, удалить", $"{CallbackPrefixes.ConfirmDeleteSessionByType}{sessionId}:{commandType}"),
                InlineKeyboardButton.WithCallbackData("↩️ Назад", $"{CallbackPrefixes.SessionDetails}{sessionId}:{commandType}")
            ]
        ]);

        await outputService.EditMessageTextWithKeyboardAsync(
            context.UserId,
            context.MessageId,
            $"Удалить все команды типа «{commandType}» из сессии #{sessionId}?",
            keyboard);

        return true;
    }

    private async Task<bool> HandleDeleteByTypeAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var arg = context.ParsedCallback.Argument;
        var parts = arg.Split(':');
        if (!int.TryParse(parts[0], out var sessionId) || sessionId <= 0 || parts.Length < 2)
        {
            LogInvalidInput("ID:Type", arg, context.Username, context.UserId);
            return true;
        }

        var commandType = parts[1];
        Logger.LogInformation("{Username} delete all commands of type {CommandType} in session {SessionId}", context.Username, commandType, sessionId);

        var deleted = await commandDataService.DeleteCommandsByTypeAsync(sessionId, commandType);
        if (deleted == 0)
        {
            Logger.LogWarning("{Username} no commands deleted for type {CommandType} session {SessionId}", context.Username, commandType, sessionId);
        }

        context.Session.SessionId = sessionId;
        var isAdmin = await CanManageAsync(context.UserId);

        if (!await sessionDataService.CheckCommandsStatusAsync(sessionId))
        {
            if (await sessionDataService.DeleteSessionAsync(sessionId, context.UserId, isAdmin))
            {
                await messageTrackingDataService.DeleteTrackedMessagesBySessionAsync(sessionId);
                context.Session.IsInStatusView = true;
                await ShowSessionsListAsync(context);
            }
        }
        else
        {
            var sessionStatus = await sessionDataService.GetSessionsStatusAsync(sessionId);
            var sessionCommands = await sessionDataService.GetSessionsCommandsAsync(sessionId);

            var newKeyboard = await keyboardBuilder.GetSessionCommandsKeyboardAsync(sessionCommands, sessionId, "ALL");
            await outputService.EditMessageTextWithKeyboardAsync(context.UserId, context.MessageId, BuildStatusReply(sessionStatus, sessionCommands), newKeyboard);
        }

        return true;
    }

    private async Task ShowSessionsListAsync(CallbackContext context)
    {
        Logger.LogInformation("{Username} view sessions", context.Username);

        var sessionsStatus = await sessionDataService.GetSessionsListAsync();
        var keyboard = await keyboardBuilder.GetSessionsListKeyboardAsync(sessionsStatus);
        await outputService.EditMessageTextWithKeyboardAsync(context.UserId, context.MessageId, "Сессии:", keyboard);
        context.Session.StatusMessageId = context.MessageId;
    }

    private static string BuildStatusReply(SessionStatus sessionStatus, List<SessionCommands>? sessionCommands = null)
    {
        var statusIcon = sessionStatus.Status switch
        {
            "Done" => "✅",
            "Failed" => "❌",
            "Deleted" => "🗑",
            _ => "🔄"
        };

        var projectName = MarkdownHelper.EscapeMarkdown(sessionStatus.ProjectName!);

        var header = $"{statusIcon} *{projectName}*\n  📅 {sessionStatus.CreatedAt:dd.MM.yyyy · HH:mm}";

        if (sessionCommands == null || sessionCommands.Count == 0)
        {
            return $"*{projectName}*\n  📅 {sessionStatus.CreatedAt:dd.MM.yyyy · HH:mm}";
        }

        var commandLines = sessionCommands
            .GroupBy(c => c.Command)
            .OrderBy(g => g.Key)
            .Select(group =>
            {
                var totalInGroup = group.Count();
                var doneInGroup = group.Count(c => c.Status == "Done");
                var failedInGroup = group.Count(c => c.Status == "Failed");
                var processingInGroup = group.Count(c => c.Status == "processing");
                var filesLabel = totalInGroup == 1 ? "файл" : "файлов";

                var groupStatusIcon = "⏳";
                if (doneInGroup == totalInGroup) groupStatusIcon = "✅";
                else if (failedInGroup > 0) groupStatusIcon = "❌";
                else if (processingInGroup > 0) groupStatusIcon = "🔄";

                return $"📦 *{group.Key}* ({doneInGroup}/{totalInGroup}) {groupStatusIcon} · {totalInGroup} {filesLabel}";
            });

        var commandsText = string.Join("\n", commandLines);

        return $"{header}\n" +
               $"━━━━━━━━━━━━━━━━━━━━\n" +
               $"{commandsText}";
    }

}
