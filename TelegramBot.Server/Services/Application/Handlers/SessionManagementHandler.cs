using System.Diagnostics.CodeAnalysis;
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
        CallbackPrefixes.DeleteCommand
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
            CallbackPrefixes.DeleteSession => await HandleDeleteSessionAsync(context, cancellationToken),
            CallbackPrefixes.DeleteCommand => await HandleDeleteCommandAsync(context, cancellationToken),
            _ => false
        };
    }

    private async Task<bool> HandleSessionDetailsAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        if (!TryParseId(context, out var sessionId))
            return true;

        var session = context.Session;

        if (session.IsInStatusView)
        {
            Logger.LogInformation("{Username} view session {SessionId}", context.Username, sessionId);
            session.IsInStatusView = false;
            session.SessionId = sessionId;

            var sessionStatus = await dataService.GetSessionsStatusAsync(sessionId);
            var keyboard = await keyboardBuilder.GetSessionStatusKeyboardAsync(sessionStatus, sessionId);
            await outputService.EditMessageTextWithKeyboardAsync(context.UserId, context.MessageId, BuildStatusReply(sessionStatus), keyboard);
            session.StatusMessageId = context.MessageId;
        }
        else
        {
            Logger.LogInformation("{Username} view cmds {SessionId}", context.Username, sessionId);
            session.IsInStatusView = true;

            var sessionCommands = await dataService.GetSessionsCommandsAsync(sessionId);
            var keyboard = await keyboardBuilder.GetSessionCommandsKeyboardAsync(sessionCommands, sessionId);
            await outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);
            session.StatusMessageId = context.MessageId;
        }

        return true;
    }

    private async Task<bool> HandleDeleteSessionAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        if (!TryParseId(context, out var sessionId))
            return true;

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

    private async Task<bool> HandleDeleteCommandAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        if (!TryParseId(context, out var commandId))
            return true;

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
            var sessionCommands = await dataService.GetSessionsCommandsAsync(sessionId.Value);
            var newKeyboard = await keyboardBuilder.GetSessionCommandsKeyboardAsync(sessionCommands, sessionId.Value);
            await outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, newKeyboard);
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

    private static string BuildStatusReply(SessionStatus sessionStatus)
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

        return $"{statusIcon} *Статус сессии{projectName}*\n" +
               $"{progressBar} {percentage}%" +
               $"\n\n📊 *Сводка:*" +
               $"\n📄 Всего: {sessionStatus.TotalFiles}" +
               $"\n✅ Готово: {sessionStatus.DoneFiles}" +
               $"\n🔄 Выполняется: {sessionStatus.ProcessingFiles}" +
               $"\n⏳ В очереди: {sessionStatus.PendingFiles}" +
               $"\n❌ Ошибок: {sessionStatus.FailedFiles}";
    }

    private static string BuildProgressBar(int percentage, int segments)
    {
        var filled = percentage * segments / 100;
        var empty = segments - filled;
        var bar = new string('█', filled) + new string('░', empty);
        return bar;
    }

}
