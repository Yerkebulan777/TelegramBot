using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Data;
using TelegramBot.Server.Helpers;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class SessionManagementHandler(
    SessionDataService sessionDataService,
    CommandDataService commandDataService,
    MessageTrackingDataService messageTrackingDataService,
    KeyboardBuilder keyboardBuilder,
    TelegramOutputService outputService,
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
        CallbackPrefixes.StatusPage,
        CallbackPrefixes.CommandsPage,
        CallbackPrefixes.RerunCommand
    ];

    public override Task HandleAsync(CallbackContext context, CancellationToken cancellationToken = default)
    {
        return context.ParsedCallback.Prefix switch
        {
            CallbackPrefixes.SessionDetails => HandleSessionDetailsAsync(context),
            CallbackPrefixes.DeleteSession => HandleDeleteSessionConfirmationAsync(context),
            CallbackPrefixes.DeleteCommand => HandleDeleteCommandConfirmationAsync(context),
            CallbackPrefixes.ConfirmDeleteSession => HandleDeleteSessionAsync(context),
            CallbackPrefixes.ConfirmDeleteCommand => HandleDeleteCommandAsync(context),
            CallbackPrefixes.DeleteSessionByType => HandleDeleteByTypeConfirmationAsync(context),
            CallbackPrefixes.ConfirmDeleteSessionByType => HandleDeleteByTypeAsync(context),
            CallbackPrefixes.StatusFilter => HandleStatusFilterAsync(context),
            CallbackPrefixes.StatusPage => HandleStatusPageAsync(context),
            CallbackPrefixes.CommandsPage => HandleCommandsPageAsync(context),
            CallbackPrefixes.RerunCommand => HandleRerunCommandAsync(context),
            _ => Task.CompletedTask
        };
    }

    // ────────────────────────── Filter ──────────────────────────

    private async Task HandleStatusFilterAsync(CallbackContext context)
    {
        var filter = string.IsNullOrEmpty(context.ParsedCallback.Argument)
            ? StatusFilters.All
            : context.ParsedCallback.Argument;

        context.Session.StatusFilter = filter;
        // Сброс страницы при смене фильтра — новый список может быть короче.
        context.Session.StatusPage = 0;
        await ShowSessionsListAsync(context);
    }

    // ────────────────────────── Page ──────────────────────────

    private async Task HandleStatusPageAsync(CallbackContext context)
    {
        if (!int.TryParse(context.ParsedCallback.Argument, out var page) || page < 0)
        {
            LogInvalidInput("page", context.ParsedCallback.Argument, context.Username, context.UserId);
            return;
        }

        context.Session.StatusPage = page;
        await ShowSessionsListAsync(context);
    }

    /// <summary>Страница списка файлов внутри сессии. Аргумент: "sessionId:filter:page".</summary>
    private async Task HandleCommandsPageAsync(CallbackContext context)
    {
        var parts = context.ParsedCallback.Argument.Split(':', 3);
        if (parts.Length < 3
            || !int.TryParse(parts[0], out var sessionId) || sessionId <= 0
            || !int.TryParse(parts[2], out var page) || page < 0)
        {
            LogInvalidInput("sessionId:filter:page", context.ParsedCallback.Argument, context.Username, context.UserId);
            return;
        }

        await RenderCommandsViewAsync(context, sessionId, parts[1], page);
    }

    /// <summary>Загружает команды сессии и редактирует сообщение клавиатурой/текстом списка файлов.</summary>
    private async Task RenderCommandsViewAsync(CallbackContext context, int sessionId, string filter, int page = 0)
    {
        context.Session.SessionId = sessionId;

        var sessionStatus = await sessionDataService.GetSessionsStatusAsync(sessionId);
        var sessionCommands = await sessionDataService.GetSessionsCommandsAsync(sessionId);
        var keyboard = keyboardBuilder.GetSessionCommandsKeyboard(sessionCommands, sessionId, filter, page);
        await outputService.EditMessageTextWithKeyboardAsync(
            context.UserId, context.MessageId, BuildStatusReply(sessionStatus, sessionCommands), keyboard);
        context.Session.StatusMessageId = context.MessageId;
    }

    // ────────────────────────── Session Details ──────────────────────────

    private async Task HandleSessionDetailsAsync(CallbackContext context)
    {
        if (!TryParseCompoundId(context, out var sessionId, out var filter))
        {
            return;
        }

        filter = string.IsNullOrEmpty(filter) ? "ALL" : filter;
        var session = context.Session;

        if (string.Equals(filter, "SUMMARY", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogInformation("{Username} view session summary {SessionId}", context.Username, sessionId);
            session.SessionId = sessionId;

            var sessionStatus = await sessionDataService.GetSessionsStatusAsync(sessionId);
            var keyboard = keyboardBuilder.GetSessionStatusKeyboard(sessionStatus, sessionId);
            await outputService.EditMessageTextWithKeyboardAsync(
                context.UserId, context.MessageId, BuildStatusReply(sessionStatus), keyboard);
            session.StatusMessageId = context.MessageId;
        }
        else
        {
            Logger.LogInformation("{Username} view cmds {SessionId} with filter {Filter}",
                context.Username, sessionId, filter);
            await RenderCommandsViewAsync(context, sessionId, filter);
        }

    }

    // ────────────────────── Delete Confirmation dialogs ──────────────────────

    private async Task HandleDeleteSessionConfirmationAsync(CallbackContext context)
    {
        if (!TryParseId(context, out var sessionId))
        {
            return;
        }

        Logger.LogInformation("{Username} requested delete confirmation for session {SessionId}",
            context.Username, sessionId);

        var keyboard = BuildConfirmationKeyboard(
            CallbackPrefixes.ConfirmDeleteSession, sessionId.ToString(),
            CallbackPrefixes.SessionDetails, sessionId.ToString());

        await outputService.EditMessageTextWithKeyboardAsync(
            context.UserId, context.MessageId,
            $"Удалить сессию #{sessionId} и все её команды?", keyboard);

    }

    private async Task HandleDeleteCommandConfirmationAsync(CallbackContext context)
    {
        if (!TryParseCompoundId(context, out var commandId, out var filter))
        {
            return;
        }

        filter = string.IsNullOrEmpty(filter) ? "ALL" : filter;
        var sessionId = await ResolveSessionByCommandAsync(context, commandId);
        if (!sessionId.HasValue)
        {
            return;
        }

        Logger.LogInformation("{Username} requested delete confirmation for command {CommandId}",
            context.Username, commandId);

        var keyboard = new InlineKeyboardMarkup([
        [
            InlineKeyboardButton.WithCallbackData("🔴 Удалить", $"{CallbackPrefixes.ConfirmDeleteCommand}{commandId}:{filter}"),
            InlineKeyboardButton.WithCallbackData("🔁 Повторить", $"{CallbackPrefixes.RerunCommand}{commandId}:{filter}")
        ],
        [
            InlineKeyboardButton.WithCallbackData("↩️ Назад", $"{CallbackPrefixes.SessionDetails}{sessionId.Value}:{filter}")
        ]]);

        await outputService.EditMessageTextWithKeyboardAsync(
            context.UserId, context.MessageId,
            $"Удалить команду #{commandId}?", keyboard);

    }

    private async Task HandleDeleteByTypeConfirmationAsync(CallbackContext context)
    {
        if (!TryParseCompoundId(context, out var sessionId, out var commandType)
            || string.IsNullOrEmpty(commandType))
        {
            LogInvalidInput("ID:Type", context.ParsedCallback.Argument, context.Username, context.UserId);
            return;
        }

        Logger.LogInformation("{Username} requested delete confirmation for type {CommandType} in session {SessionId}",
            context.Username, commandType, sessionId);

        var keyboard = BuildConfirmationKeyboard(
            CallbackPrefixes.ConfirmDeleteSessionByType, $"{sessionId}:{commandType}",
            CallbackPrefixes.SessionDetails, $"{sessionId}:{commandType}");

        await outputService.EditMessageTextWithKeyboardAsync(
            context.UserId, context.MessageId,
            $"Удалить все команды типа «{commandType}» из сессии #{sessionId}?", keyboard);

    }

    // ────────────────────── Delete Execution ──────────────────────

    private async Task HandleDeleteSessionAsync(CallbackContext context)
    {
        if (!TryParseId(context, out var sessionId))
        {
            return;
        }

        Logger.LogInformation("{Username} delete session {SessionId}", context.Username, sessionId);

        if (!await DeleteSessionAndMessagesAsync(sessionId, context.UserId))
        {
            return;
        }

        await ShowSessionsListAsync(context);

    }

    private async Task HandleDeleteCommandAsync(CallbackContext context)
    {
        if (!TryParseCompoundId(context, out var commandId, out var filter))
        {
            return;
        }

        filter = string.IsNullOrEmpty(filter) ? "ALL" : filter;
        Logger.LogInformation("{Username} delete cmd {CommandId}", context.Username, commandId);

        var sessionId = await ResolveSessionByCommandAsync(context, commandId);
        if (!sessionId.HasValue || !await commandDataService.DeleteCommandAsync(commandId, context.UserId, IsAdmin))
        {
            return;
        }

        context.Session.SessionId = sessionId.Value;
        await HandlePostDeletionAsync(context, sessionId.Value, filter);

    }

    private async Task HandleDeleteByTypeAsync(CallbackContext context)
    {
        if (!TryParseCompoundId(context, out var sessionId, out var commandType)
            || string.IsNullOrEmpty(commandType))
        {
            LogInvalidInput("ID:Type", context.ParsedCallback.Argument, context.Username, context.UserId);
            return;
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

    }

    // ────────────────────── Rerun ──────────────────────

    /// <summary>Повторный запуск файла; блокируется, если файл сейчас 'processing'.</summary>
    private async Task HandleRerunCommandAsync(CallbackContext context)
    {
        if (!TryParseCompoundId(context, out var commandId, out var filter))
        {
            return;
        }

        filter = string.IsNullOrEmpty(filter) ? "ALL" : filter;
        Logger.LogInformation("{Username} rerun cmd {CommandId}", context.Username, commandId);

        var sessionId = await ResolveSessionByCommandAsync(context, commandId);
        if (!sessionId.HasValue)
        {
            return;
        }

        var outcome = await commandDataService.RequeueCommandAsync(commandId, context.UserId, IsAdmin);
        if (outcome == RequeueOutcome.NotFound)
        {
            return;
        }

        var toast = outcome == RequeueOutcome.Processing ? "⏳ Уже выполняется" : "🔁 Перезапущено";
        await outputService.AnswerCallbackAsync(context.CallbackQueryId, toast);
        context.Session.SessionId = sessionId.Value;
        await RenderCommandsViewAsync(context, sessionId.Value, filter);
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

    /// <summary>Строит inline-клавиатуру подтверждения: "🔴 Удалить" + "↩️ Назад".</summary>
    private static InlineKeyboardMarkup BuildConfirmationKeyboard(
        string confirmPrefix, string confirmArg, string backPrefix, string backArg)
    {
        return new InlineKeyboardMarkup([
        [
            InlineKeyboardButton.WithCallbackData("🔴 Удалить", $"{confirmPrefix}{confirmArg}"),
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
            await RenderCommandsViewAsync(context, sessionId, filter);
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
