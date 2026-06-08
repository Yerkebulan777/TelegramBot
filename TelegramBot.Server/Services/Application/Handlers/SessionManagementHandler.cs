using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;
using TelegramBot.Server.Interfaces;
using System.Diagnostics.CodeAnalysis;

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
        CallbackPrefixes.BackToStatus,
        CallbackPrefixes.CancelCommand,
        CallbackPrefixes.ConfirmCancelCmd
    ];

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
            CallbackPrefixes.CancelCommand => await HandleCancelCommandAsync(context, cancellationToken),
            CallbackPrefixes.ConfirmCancelCmd => await HandleConfirmCancelAsync(context, cancellationToken),
            CallbackPrefixes.BackToStatus => await HandleBackToStatusAsync(context, cancellationToken),
            _ => false
        };
    }

    private async Task<bool> HandleSessionDetailsAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        if (!TryParseId(context, out var sessionId))
            return true;

        var session = context.Session;

        Logger.LogDebug("User {Username} ({UserId}) viewing session details for session {SessionId}", context.Username, context.UserId, sessionId);

        if (session.IsInStatusView)
        {
            session.IsInStatusView = false;
            session.SessionId = sessionId;

            var sessionStatus = await dataService.GetSessionsStatusAsync(sessionId, context.UserId);
            var keyboard = await keyboardBuilder.GetSessionStatusKeyboardAsync(sessionStatus, sessionId);
            await outputService.EditMessageTextWithKeyboardAsync(context.UserId, context.MessageId, BuildStatusReply(sessionStatus), keyboard);
            session.StatusMessageId = context.MessageId;
            await SendStatusActionsReplyKeyboardAsync(context);
        }
        else
        {
            session.IsInStatusView = true;

            var sessionCommands = await dataService.GetSessionsCommandsAsync(sessionId, context.UserId);
            var keyboard = await keyboardBuilder.GetSessionCommandsKeyboardAsync(sessionCommands, sessionId);
            await outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);
            session.StatusMessageId = context.MessageId;
            await SendStatusActionsReplyKeyboardAsync(context);
        }

        return true;
    }

    private async Task<bool> HandleDeleteSessionAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        if (!TryParseId(context, out var sessionId))
            return true;

        Logger.LogDebug("User {Username} ({UserId}) deleting session {SessionId}", context.Username, context.UserId, sessionId);

        if (!await dataService.DeleteSessionAsync(sessionId, context.UserId))
        {
            return true;
        }

        context.Session.IsInStatusView = true;
        await ShowSessionsListAsync(context);

        return true;
    }

    private async Task<bool> HandleDeleteCommandAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        if (!TryParseId(context, out var commandId))
            return true;

        Logger.LogDebug("User {Username} ({UserId}) deleting command {CommandId}", context.Username, context.UserId, commandId);

        var sessionId = await dataService.GetSessionIdByCommandAsync(commandId, context.UserId);
        if (!sessionId.HasValue)
        {
            Logger.LogWarning("User {Username} ({UserId}) attempted to access foreign or missing command {CommandId}", context.Username, context.UserId, commandId);
            return true;
        }

        if (!await dataService.DeleteCommandAsync(commandId, context.UserId))
        {
            return true;
        }

        context.Session.SessionId = sessionId.Value;

        if (!await dataService.CheckCommandsStatusAsync(sessionId.Value, context.UserId))
        {
            if (await dataService.DeleteSessionAsync(sessionId.Value, context.UserId))
            {
                context.Session.IsInStatusView = true;
                await ShowSessionsListAsync(context);
            }
        }
        else
        {
            var sessionCommands = await dataService.GetSessionsCommandsAsync(sessionId.Value, context.UserId);
            var newKeyboard = await keyboardBuilder.GetSessionCommandsKeyboardAsync(sessionCommands, sessionId.Value);
            await outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, newKeyboard);
        }

        return true;
    }

    private async Task<bool> HandleBackToStatusAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        Logger.LogDebug("User {Username} ({UserId}) returning to sessions list", context.Username, context.UserId);
        context.Session.IsInStatusView = true;
        await ShowSessionsListAsync(context);
        return true;
    }

    private async Task<bool> HandleCancelCommandAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        if (!TryParseId(context, out var commandId))
            return true;

        Logger.LogDebug("User {Username} ({UserId}) initiating cancel for command {CommandId}", context.Username, context.UserId, commandId);

        // Проверяем, принадлежит ли команда пользователю
        var command = await dataService.GetCommandByIdAsync(commandId, context.UserId);
        if (command == null)
        {
            Logger.LogWarning("User {Username} ({UserId}) attempted to cancel foreign/missing command {CommandId}",
                context.Username, context.UserId, commandId);
            return true;
        }

        // Переключаем IsInStatusView = false, чтобы "Нет" (SESSIONDETAILS) попало в ветку показа команд
        context.Session.IsInStatusView = false;

        // Показываем диалог подтверждения
        var confirmKeyboard = new InlineKeyboardMarkup(
        [
            [
                InlineKeyboardButton.WithCallbackData("✅ Да, отменить", $"{CallbackPrefixes.ConfirmCancelCmd}{commandId}"),
                InlineKeyboardButton.WithCallbackData("❌ Нет", $"{CallbackPrefixes.SessionDetails}{command.SessionId}")
            ]
        ]);

        await outputService.EditMessageTextWithKeyboardAsync(
            context.UserId,
            context.MessageId,
            $"❓ Вы уверены, что хотите отменить команду *#{command.CommandId} ({command.CommandText})*?",
            confirmKeyboard);

        return true;
    }

    private async Task<bool> HandleConfirmCancelAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        if (!TryParseId(context, out var commandId))
            return true;

        Logger.LogDebug("User {Username} ({UserId}) confirmed cancel for command {CommandId}", context.Username, context.UserId, commandId);

        // Проверяем, принадлежит ли команда пользователю
        var command = await dataService.GetCommandByIdAsync(commandId, context.UserId);
        if (command == null)
        {
            Logger.LogWarning("User {Username} ({UserId}) attempted to cancel foreign/missing command {CommandId}",
                context.Username, context.UserId, commandId);
            return true;
        }

        // Обновляем статус в БД на Cancelled
        var cancelled = await dataService.CancelCommandAsync(commandId, context.UserId);
        if (!cancelled)
        {
            Logger.LogWarning("Cancel failed or command already finished: commandId={CommandId}", commandId);
            await outputService.SendMessageAsync(context.UserId,
                "⛔ Не удалось отменить команду (возможно, она уже завершена).");
            return true;
        }

        // Уведомляем Worker о необходимости принудительно завершить процесс
        await dataService.NotifyCommandCancelAsync(commandId);

        Logger.LogInformation("Command cancelled: commandId={CommandId}, userId={UserId}",
            commandId, context.UserId);

        // Уведомляем пользователя
        await outputService.SendMessageAsync(context.UserId,
            $"⛔ Команда #{commandId} ({command.CommandText}) отменена.");

        // Обновляем отображение сессии
        context.Session.SessionId = command.SessionId;
        var sessionCommands = await dataService.GetSessionsCommandsAsync(command.SessionId, context.UserId);
        var keyboard = await keyboardBuilder.GetSessionCommandsKeyboardAsync(sessionCommands, command.SessionId);

        // Проверяем, остались ли ещё активные команды в сессии
        var hasActive = sessionCommands.Any(c =>
            c.Status == "pending" || c.Status == "processing");

        if (!hasActive)
        {
            // Если активных команд не осталось — показываем статус сессии
            var sessionStatus = await dataService.GetSessionsStatusAsync(command.SessionId, context.UserId);
            await outputService.EditMessageTextWithKeyboardAsync(
                context.UserId, context.MessageId, BuildStatusReply(sessionStatus), keyboard);
        }
        else
        {
            await outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);
        }

        return true;
    }

    private async Task ShowSessionsListAsync(CallbackContext context)
    {
        var sessionsStatus = await dataService.GetSessionsListAsync(context.UserId);
        var keyboard = await keyboardBuilder.GetSessionsListKeyboardAsync(sessionsStatus);
        await outputService.EditMessageTextWithKeyboardAsync(context.UserId, context.MessageId, "Сессии:", keyboard);
        context.Session.StatusMessageId = context.MessageId;

        var clearKeyboardMessage = await outputService.RemoveReplyKeyboardAsync(context.UserId, "Сессии:");
        if (clearKeyboardMessage != null)
        {
            context.Session.TrackMessage(clearKeyboardMessage.Id);
        }
    }

    private async Task SendStatusActionsReplyKeyboardAsync(CallbackContext context)
    {
        var replyKeyboard = await keyboardBuilder.GetStatusActionsReplyKeyboardAsync();
        var message = await outputService.SendMessageWithReplyKeyboardAsync(context.UserId, "Действия:", replyKeyboard);
        if (message != null)
        {
            context.Session.TrackMessage(message.Id);
        }
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
            "Cancelled" => "🚫",
            "Deleted" => "🗑",
            _ => "🔄"
        };

        var progressBar = BuildProgressBar(percentage, 10);

        return $"{statusIcon} *Статус сессии*\n" +
               $"{progressBar} {percentage}%" +
               $"\n\n📊 *Сводка:*" +
               $"\n📄 Всего: {sessionStatus.TotalFiles}" +
               $"\n✅ Готово: {sessionStatus.DoneFiles}" +
               $"\n🔄 Выполняется: {sessionStatus.ProcessingFiles}" +
               $"\n⏳ В очереди: {sessionStatus.PendingFiles}" +
               $"\n❌ Ошибок: {sessionStatus.FailedFiles}" +
               $"\n🚫 Отменено: {sessionStatus.CancelledFiles}";
    }

    private static string BuildProgressBar(int percentage, int segments)
    {
        var filled = percentage * segments / 100;
        var empty = segments - filled;
        var bar = new string('█', filled) + new string('░', empty);
        return bar;
    }

}
