using System.Diagnostics.CodeAnalysis;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class SessionManagementHandler(
    IDataService dataService,
    IKeyboardBuilder keyboardBuilder,
    ITelegramOutputService outputService,
    ILogger<SessionManagementHandler> logger) : CallbackHandlerBase(logger)
{
    protected override HashSet<string> SupportedPrefixes { get; } =
    [
        CallbackPrefixes.SessionDetails,
        CallbackPrefixes.DeleteSession,
        CallbackPrefixes.DeleteCommand,
        CallbackPrefixes.ConfirmDeleteSession,
        CallbackPrefixes.ConfirmDeleteCommand
    ];

    /// <summary>Проверяет, имеет ли пользователь доступ (все одобренные могут управлять любыми сессиями).</summary>
    private async Task<bool> CanManageAsync(long userId)
    {
        var user = await dataService.GetUserAsync(userId);
        return user?.Status == UserAccessStatus.Approved;
    }

    /// <summary>
    /// Пытается распарсить положительный int из callback-аргумента.
    /// При неудаче логирует через LogInvalidInput и возвращает false.
    /// </summary>
    private bool TryParseId(CallbackContext context, [NotNullWhen(true)] out int id)
    {
        if (!int.TryParse(context.ParsedCallback.Argument, out id) || id <= 0)
        {
            LogInvalidInput("ID", context.ParsedCallback.Argument, context.Username, context.UserId);
            return false;
        }
        return true;
    }

    protected override async Task<bool> HandleAsyncInternal(CallbackContext context, CancellationToken cancellationToken = default)
    {
        return context.ParsedCallback.Prefix switch
        {
            CallbackPrefixes.SessionDetails => await HandleSessionDetailsAsync(context, cancellationToken),
            CallbackPrefixes.DeleteSession => await HandleDeleteSessionConfirmationAsync(context, cancellationToken),
            CallbackPrefixes.DeleteCommand => await HandleDeleteCommandConfirmationAsync(context, cancellationToken),
            CallbackPrefixes.ConfirmDeleteSession => await HandleDeleteSessionAsync(context, cancellationToken),
            CallbackPrefixes.ConfirmDeleteCommand => await HandleDeleteCommandAsync(context, cancellationToken),
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

            var sessionStatus = await dataService.GetSessionsStatusAsync(sessionId);
            var keyboard = await keyboardBuilder.GetSessionStatusKeyboardAsync(sessionStatus, sessionId);
            await outputService.EditMessageTextWithKeyboardAsync(context.UserId, context.MessageId, BuildStatusReply(sessionStatus), keyboard);
            session.StatusMessageId = context.MessageId;
        }
        else
        {
            Logger.LogInformation("{Username} view cmds {SessionId} with filter {Filter}", context.Username, sessionId, filter);
            session.IsInStatusView = true;
            session.SessionId = sessionId;

            var sessionStatus = await dataService.GetSessionsStatusAsync(sessionId);
            var sessionCommands = await dataService.GetSessionsCommandsAsync(sessionId);
            var keyboard = await keyboardBuilder.GetSessionCommandsKeyboardAsync(sessionCommands, sessionId, filter);
            await outputService.EditMessageTextWithKeyboardAsync(context.UserId, context.MessageId, BuildStatusReply(sessionStatus, sessionCommands, filter), keyboard);
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
        if (!await dataService.DeleteSessionAsync(sessionId, context.UserId, isAdmin))
        {
            return true;
        }

        // Очищаем tracked messages из БД
        await dataService.DeleteTrackedMessagesBySessionAsync(sessionId);

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
        var sessionId = await dataService.GetSessionIdByCommandAsync(commandId, context.UserId, isAdmin);
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
        var sessionId = await dataService.GetSessionIdByCommandAsync(commandId, context.UserId, isAdmin);
        if (!sessionId.HasValue)
        {
            Logger.LogWarning("{Username} foreign cmd {CommandId}", context.Username, commandId);
            return true;
        }

        if (!await dataService.DeleteCommandAsync(commandId, context.UserId, isAdmin))
        {
            return true;
        }

        context.Session.SessionId = sessionId.Value;

        if (!await dataService.CheckCommandsStatusAsync(sessionId.Value))
        {
            // Последняя команда — удаляем сессию и tracked messages
            if (await dataService.DeleteSessionAsync(sessionId.Value, context.UserId, isAdmin))
            {
                await dataService.DeleteTrackedMessagesBySessionAsync(sessionId.Value);
                context.Session.IsInStatusView = true;
                await ShowSessionsListAsync(context);
            }
        }
        else
        {
            var sessionStatus = await dataService.GetSessionsStatusAsync(sessionId.Value);
            var sessionCommands = await dataService.GetSessionsCommandsAsync(sessionId.Value);
            var newKeyboard = await keyboardBuilder.GetSessionCommandsKeyboardAsync(sessionCommands, sessionId.Value, filter);
            await outputService.EditMessageTextWithKeyboardAsync(context.UserId, context.MessageId, BuildStatusReply(sessionStatus, sessionCommands, filter), newKeyboard);
        }

        return true;
    }

    private async Task ShowSessionsListAsync(CallbackContext context)
    {
        Logger.LogInformation("{Username} view sessions", context.Username);

        var sessionsStatus = await dataService.GetSessionsListAsync();
        var keyboard = await keyboardBuilder.GetSessionsListKeyboardAsync(sessionsStatus);
        await outputService.EditMessageTextWithKeyboardAsync(context.UserId, context.MessageId, "Сессии:", keyboard);
        context.Session.StatusMessageId = context.MessageId;
    }

    private static string BuildStatusReply(SessionStatus sessionStatus, List<SessionCommands>? sessionCommands = null, string selectedFilter = "ALL")
    {
        var percentage = sessionStatus.TotalFiles > 0
            ? 100 * sessionStatus.DoneFiles / sessionStatus.TotalFiles
            : 0;

        var statusIcon = sessionStatus.Status switch
        {
            "Done" => "✅",
            "Failed" => "❌",
            "Deleted" => "🗑",
            _ => "🔄"
        };

        var progressBar = BuildProgressBar(percentage, 10);
        var projectName = string.IsNullOrEmpty(sessionStatus.ProjectName) ? "" : $" — {sessionStatus.ProjectName}";

        // Summary view (no commands provided)
        if (sessionCommands == null || sessionCommands.Count == 0)
        {
            return $"{statusIcon} *Статус сессии{projectName}*\n" +
                   $"{progressBar} {percentage}%" +
                   $"\n\n📊 *Сводка:*" +
                   $"\n📄 Всего: {sessionStatus.TotalFiles}" +
                   $"\n✅ Готово: {sessionStatus.DoneFiles}" +
                   $"\n🔄 Выполняется: {sessionStatus.ProcessingFiles}" +
                   $"\n⏳ В очереди: {sessionStatus.PendingFiles}" +
                   $"\n❌ Ошибок: {sessionStatus.FailedFiles}";
        }

        // Detailed commands view grouped by Command Type
        var isAllSelected = string.IsNullOrEmpty(selectedFilter) || selectedFilter == "ALL";
        var groupedCommands = sessionCommands
            .GroupBy(c => c.Command)
            .OrderBy(g => g.Key)
            .ToList();

        var commandLines = new List<string>();

        foreach (var group in groupedCommands)
        {
            var groupKey = group.Key;

            // If a specific filter is selected, skip other groups
            if (!isAllSelected && !string.Equals(groupKey, selectedFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var totalInGroup = group.Count();
            var doneInGroup = group.Count(c => c.Status == "Done");
            var failedInGroup = group.Count(c => c.Status == "Failed");
            var processingInGroup = group.Count(c => c.Status == "processing");

            var groupStatusIcon = "⏳";
            if (doneInGroup == totalInGroup) groupStatusIcon = "✅";
            else if (failedInGroup > 0) groupStatusIcon = "❌";
            else if (processingInGroup > 0) groupStatusIcon = "🔄";

            commandLines.Add($"📦 *{groupKey}* ({doneInGroup}/{totalInGroup}) {groupStatusIcon}");

            var groupList = group.OrderBy(c => c.ExecOrder).ToList();
            for (int i = 0; i < groupList.Count; i++)
            {
                var cmd = groupList[i];
                var isLast = i == groupList.Count - 1;
                var treeIcon = isLast ? "└─" : "├─";

                var statusIconCmd = cmd.Status switch
                {
                    "Done" => "✅",
                    "Failed" => "❌",
                    "processing" => "🔄",
                    "pending" => "⏳",
                    "Deleted" => "🗑",
                    _ => "❓"
                };

                var fileName = Path.GetFileName(cmd.FileName);
                var timeStr = cmd.Date != default ? $" ({cmd.Date:HH:mm:ss})" : "";

                commandLines.Add($"{treeIcon} {cmd.ExecOrder}. {statusIconCmd} {fileName}{timeStr}");
            }

            // Add an empty line between groups for readability
            commandLines.Add(string.Empty);
        }

        // Remove the last empty line if any
        if (commandLines.Count > 0 && string.IsNullOrEmpty(commandLines[^1]))
        {
            commandLines.RemoveAt(commandLines.Count - 1);
        }

        var commandsText = string.Join("\n", commandLines);

        return $"{statusIcon} *Статус сессии{projectName}*\n" +
               $"{progressBar} {percentage}%\n" +
               $"━━━━━━━━━━━━━━━━━━━━\n" +
               $"{commandsText}";
    }

    private static string BuildProgressBar(int percentage, int segments)
    {
        var filled = percentage * segments / 100;
        var empty = segments - filled;
        var bar = new string('█', filled) + new string('░', empty);
        return bar;
    }

}
