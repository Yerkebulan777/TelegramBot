using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Data;
using TelegramBot.Server.Helpers;
using TelegramBot.Server.Interfaces;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class SessionManagementHandler(
    SessionDataService sessionDataService,
    CommandDataService commandDataService,
    MessageTrackingDataService messageTrackingDataService,
    KeyboardBuilder keyboardBuilder,
    ITelegramOutputService outputService,
    SessionsListRenderer sessionsListRenderer,
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
        CallbackPrefixes.ConfirmDeleteSessionByType,
        CallbackPrefixes.StatusFilter,
        CallbackPrefixes.StatusPage
    ];

    public override async Task<bool> HandleAsync(CallbackContext context, CancellationToken cancellationToken = default)
    {
        return context.ParsedCallback.Prefix switch
        {
            CallbackPrefixes.SessionDetails => await HandleSessionDetailsAsync(context),
            CallbackPrefixes.DeleteSession => await HandleDeleteSessionConfirmationAsync(context),
            CallbackPrefixes.DeleteCommand => await HandleDeleteCommandConfirmationAsync(context),
            CallbackPrefixes.ConfirmDeleteSession => await HandleDeleteSessionAsync(context),
            CallbackPrefixes.ConfirmDeleteCommand => await HandleDeleteCommandAsync(context),
            CallbackPrefixes.DeleteSessionByType => await HandleDeleteByTypeConfirmationAsync(context),
            CallbackPrefixes.ConfirmDeleteSessionByType => await HandleDeleteByTypeAsync(context),
            CallbackPrefixes.StatusFilter => await HandleStatusFilterAsync(context),
            CallbackPrefixes.StatusPage => await HandleStatusPageAsync(context),
            _ => false
        };
    }

    // ────────────────────────── Filter ──────────────────────────

    private async Task<bool> HandleStatusFilterAsync(CallbackContext context)
    {
        var filter = string.IsNullOrEmpty(context.ParsedCallback.Argument)
            ? StatusFilters.All
            : context.ParsedCallback.Argument;

        context.Session.StatusFilter = filter;
        // Сброс страницы при смене фильтра — новый список может быть короче.
        context.Session.StatusPage = 0;
        await ShowSessionsListAsync(context);
        return true;
    }

    // ────────────────────────── Page ──────────────────────────

    private async Task<bool> HandleStatusPageAsync(CallbackContext context)
    {
        if (!int.TryParse(context.ParsedCallback.Argument, out var page) || page < 0)
        {
            LogInvalidInput("page", context.ParsedCallback.Argument, context.Username, context.UserId);
            return true;
        }

        context.Session.StatusPage = page;
        await ShowSessionsListAsync(context);
        return true;
    }

    // ────────────────────────── Session Details ──────────────────────────

    private async Task<bool> HandleSessionDetailsAsync(CallbackContext context)
    {
        if (!TryParseCompoundId(context, out var sessionId, out var filter))
        {
            return true;
        }

        filter = string.IsNullOrEmpty(filter) ? "ALL" : filter;
        var session = context.Session;

        if (string.Equals(filter, "SUMMARY", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogInformation("{Username} view session summary {SessionId}", context.Username, sessionId);
            session.SessionId = sessionId;

            var sessionStatus = await sessionDataService.GetSessionsStatusAsync(sessionId);
            var keyboard = await keyboardBuilder.GetSessionStatusKeyboardAsync(sessionStatus, sessionId);
            await outputService.EditMessageTextWithKeyboardAsync(
                context.UserId, context.MessageId, BuildStatusReply(sessionStatus), keyboard);
            session.StatusMessageId = context.MessageId;
        }
        else
        {
            Logger.LogInformation("{Username} view cmds {SessionId} with filter {Filter}",
                context.Username, sessionId, filter);
            session.SessionId = sessionId;

            var sessionStatus = await sessionDataService.GetSessionsStatusAsync(sessionId);
            var sessionCommands = await sessionDataService.GetSessionsCommandsAsync(sessionId);

            var keyboard = await keyboardBuilder.GetSessionCommandsKeyboardAsync(sessionCommands, sessionId, filter);
            await outputService.EditMessageTextWithKeyboardAsync(
                context.UserId, context.MessageId, BuildStatusReply(sessionStatus, sessionCommands), keyboard);
            session.StatusMessageId = context.MessageId;
        }

        return true;
    }

    // ────────────────────── Delete Confirmation dialogs ──────────────────────

    private async Task<bool> HandleDeleteSessionConfirmationAsync(CallbackContext context)
    {
        if (!TryParseId(context, out var sessionId))
        {
            return true;
        }

        Logger.LogInformation("{Username} requested delete confirmation for session {SessionId}",
            context.Username, sessionId);

        var keyboard = BuildConfirmationKeyboard(
            CallbackPrefixes.ConfirmDeleteSession, sessionId.ToString(),
            CallbackPrefixes.SessionDetails, sessionId.ToString());

        await outputService.EditMessageTextWithKeyboardAsync(
            context.UserId, context.MessageId,
            $"Удалить сессию #{sessionId} и все её команды?", keyboard);

        return true;
    }

    private async Task<bool> HandleDeleteCommandConfirmationAsync(CallbackContext context)
    {
        if (!TryParseCompoundId(context, out var commandId, out var filter))
        {
            return true;
        }

        filter = string.IsNullOrEmpty(filter) ? "ALL" : filter;
        var sessionId = await ResolveSessionByCommandAsync(context, commandId);
        if (!sessionId.HasValue)
        {
            return true;
        }

        Logger.LogInformation("{Username} requested delete confirmation for command {CommandId}",
            context.Username, commandId);

        var keyboard = BuildConfirmationKeyboard(
            CallbackPrefixes.ConfirmDeleteCommand, $"{commandId}:{filter}",
            CallbackPrefixes.SessionDetails, $"{sessionId.Value}:{filter}");

        await outputService.EditMessageTextWithKeyboardAsync(
            context.UserId, context.MessageId,
            $"Удалить команду #{commandId}?", keyboard);

        return true;
    }

    private async Task<bool> HandleDeleteByTypeConfirmationAsync(CallbackContext context)
    {
        if (!TryParseCompoundId(context, out var sessionId, out var commandType)
            || string.IsNullOrEmpty(commandType))
        {
            LogInvalidInput("ID:Type", context.ParsedCallback.Argument, context.Username, context.UserId);
            return true;
        }

        Logger.LogInformation("{Username} requested delete confirmation for type {CommandType} in session {SessionId}",
            context.Username, commandType, sessionId);

        var keyboard = BuildConfirmationKeyboard(
            CallbackPrefixes.ConfirmDeleteSessionByType, $"{sessionId}:{commandType}",
            CallbackPrefixes.SessionDetails, $"{sessionId}:{commandType}");

        await outputService.EditMessageTextWithKeyboardAsync(
            context.UserId, context.MessageId,
            $"Удалить все команды типа «{commandType}» из сессии #{sessionId}?", keyboard);

        return true;
    }

    // ────────────────────── Delete Execution ──────────────────────

    private async Task<bool> HandleDeleteSessionAsync(CallbackContext context)
    {
        if (!TryParseId(context, out var sessionId))
        {
            return true;
        }

        Logger.LogInformation("{Username} delete session {SessionId}", context.Username, sessionId);

        if (!await DeleteSessionAndMessagesAsync(sessionId, context.UserId))
        {
            return true;
        }

        await ShowSessionsListAsync(context);

        return true;
    }

    private async Task<bool> HandleDeleteCommandAsync(CallbackContext context)
    {
        if (!TryParseCompoundId(context, out var commandId, out var filter))
        {
            return true;
        }

        filter = string.IsNullOrEmpty(filter) ? "ALL" : filter;
        Logger.LogInformation("{Username} delete cmd {CommandId}", context.Username, commandId);

        var sessionId = await ResolveSessionByCommandAsync(context, commandId);
        if (!sessionId.HasValue || !await commandDataService.DeleteCommandAsync(commandId, context.UserId, IsAdmin))
        {
            return true;
        }

        context.Session.SessionId = sessionId.Value;
        await HandlePostDeletionAsync(context, sessionId.Value, filter);

        return true;
    }

    private async Task<bool> HandleDeleteByTypeAsync(CallbackContext context)
    {
        if (!TryParseCompoundId(context, out var sessionId, out var commandType)
            || string.IsNullOrEmpty(commandType))
        {
            LogInvalidInput("ID:Type", context.ParsedCallback.Argument, context.Username, context.UserId);
            return true;
        }

        Logger.LogInformation("{Username} delete all commands of type {CommandType} in session {SessionId}",
            context.Username, commandType, sessionId);

        var deleted = await commandDataService.DeleteCommandsByTypeAsync(sessionId, commandType);
        if (deleted == 0)
        {
            Logger.LogWarning("{Username} no commands deleted for type {CommandType} session {SessionId}",
                context.Username, commandType, sessionId);
        }

        context.Session.SessionId = sessionId;
        await HandlePostDeletionAsync(context, sessionId, "ALL");

        return true;
    }

    // ────────────────────── Shared Helpers ──────────────────────

    private async Task<bool> DeleteSessionAndMessagesAsync(int sessionId, long userId)
    {
        if (!await sessionDataService.DeleteSessionAsync(sessionId, userId, IsAdmin))
        {
            return false;
        }

        await messageTrackingDataService.DeleteTrackedMessagesBySessionAsync(sessionId);
        return true;
    }

    /// <summary>Парсит аргумент вида "id:suffix", возвращает id и suffix.</summary>
    private bool TryParseCompoundId(CallbackContext context, out int id, out string suffix)
    {
        var arg = context.ParsedCallback.Argument;
        var parts = arg.Split(':', 2);
        if (!int.TryParse(parts[0], out id) || id <= 0)
        {
            LogInvalidInput("ID", arg, context.Username, context.UserId);
            suffix = "";
            return false;
        }

        suffix = parts.Length > 1 ? parts[1] : "";
        return true;
    }

    /// <summary>Строит inline-клавиатуру подтверждения: "✅ Да, удалить" + "↩️ Назад".</summary>
    private static InlineKeyboardMarkup BuildConfirmationKeyboard(
        string confirmPrefix, string confirmArg, string backPrefix, string backArg)
    {
        return new InlineKeyboardMarkup([
        [
            InlineKeyboardButton.WithCallbackData("✅ Да, удалить", $"{confirmPrefix}{confirmArg}"),
            InlineKeyboardButton.WithCallbackData("↩️ Назад", $"{backPrefix}{backArg}")
        ]]);
    }

    /// <summary>
    /// После удаления команды(д): если сессия пуста — удаляем её и tracked messages,
    /// иначе обновляем отображение команд.
    /// </summary>
    private async Task HandlePostDeletionAsync(CallbackContext context, int sessionId, string filter)
    {
        if (!await sessionDataService.CheckCommandsStatusAsync(sessionId))
        {
            if (await DeleteSessionAndMessagesAsync(sessionId, context.UserId))
            {
                await ShowSessionsListAsync(context);
            }
        }
        else
        {
            var sessionStatus = await sessionDataService.GetSessionsStatusAsync(sessionId);
            var sessionCommands = await sessionDataService.GetSessionsCommandsAsync(sessionId);

            var newKeyboard = await keyboardBuilder.GetSessionCommandsKeyboardAsync(sessionCommands, sessionId, filter);
            await outputService.EditMessageTextWithKeyboardAsync(
                context.UserId, context.MessageId,
                BuildStatusReply(sessionStatus, sessionCommands), newKeyboard);
        }
    }

    /// <summary>Резолвит SessionId по CommandId с проверкой прав.</summary>
    private async Task<int?> ResolveSessionByCommandAsync(CallbackContext context, int commandId)
    {
        var sessionId = await sessionDataService.GetSessionIdByCommandAsync(commandId, context.UserId, IsAdmin);
        if (!sessionId.HasValue)
        {
            Logger.LogWarning("{Username} foreign or missing cmd {CommandId}", context.Username, commandId);
        }
        return sessionId;
    }

    // Все одобренные пользователи могут управлять любыми сессиями (проверка доступа — в AuthorizationMiddleware).
    // Для SQL-параметра @IsAdmin достаточно true.
    private static bool IsAdmin => true;

    private async Task ShowSessionsListAsync(CallbackContext context)
    {
        var session = context.Session;
        var targetMessageId = session.StatusMessageId ?? context.MessageId;
        await sessionsListRenderer.EditExistingAsync(
            context.UserId, targetMessageId, session.StatusFilter, session.StatusPage, context.Username);
        session.StatusMessageId ??= context.MessageId;
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

        var projectName = MarkdownHelper.Escape(sessionStatus.ProjectName!);

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
                if (doneInGroup == totalInGroup)
                {
                    groupStatusIcon = "✅";
                }
                else if (failedInGroup > 0)
                {
                    groupStatusIcon = "❌";
                }
                else if (processingInGroup > 0)
                {
                    groupStatusIcon = "🔄";
                }

                return $"📦 *{group.Key}* ({doneInGroup}/{totalInGroup}) {groupStatusIcon} · {totalInGroup} {filesLabel}";
            });

        var commandsText = string.Join("\n", commandLines);

        return $"{header}\n" +
               $"━━━━━━━━━━━━━━━━━━━━\n" +
               $"{commandsText}";
    }
}
