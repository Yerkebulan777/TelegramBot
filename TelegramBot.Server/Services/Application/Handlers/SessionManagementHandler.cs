using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.DTOs;
using TelegramBot.Core.Extensions;
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
    private readonly IDataService _dataService = dataService;
    private readonly IKeyboardBuilder _keyboardBuilder = keyboardBuilder;
    private readonly ITelegramOutputService _outputService = outputService;

    protected override HashSet<string> SupportedPrefixes { get; } =
    [
        CallbackPrefixes.SessionDetails,
        CallbackPrefixes.DeleteSession,
        CallbackPrefixes.DeleteCommand,
        CallbackPrefixes.BackToStatus,
        CallbackPrefixes.CancelCommand,
        CallbackPrefixes.ConfirmCancelCmd
    ];

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
        var token = context.ParsedCallback.Argument;
        if (!int.TryParse(token, out var sessionId) || !sessionId.IsValidId())
        {
            LogInvalidInput("session ID", token, context.Username, context.UserId);
            return true;
        }

        var session = context.Session;

        Logger.LogDebug("User {Username} ({UserId}) viewing session details for session {SessionId}", context.Username, context.UserId, sessionId);

        if (session.IsInStatusView)
        {
            session.IsInStatusView = false;
            session.SessionId = sessionId;

            var sessionStatus = await _dataService.GetSessionsStatusAsync(sessionId, context.UserId);
            var keyboard = await _keyboardBuilder.GetSessionStatusKeyboardAsync(sessionStatus, sessionId);
            await _outputService.EditMessageTextWithKeyboardAsync(context.UserId, context.MessageId, BuildStatusReply(sessionStatus), keyboard);
            session.StatusMessageId = context.MessageId;
            await SendStatusActionsReplyKeyboardAsync(context);
        }
        else
        {
            session.IsInStatusView = true;

            var sessionCommands = await _dataService.GetSessionsCommandsAsync(sessionId, context.UserId);
            var keyboard = await _keyboardBuilder.GetSessionCommandsKeyboardAsync(sessionCommands, sessionId);
            await _outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);
            session.StatusMessageId = context.MessageId;
            await SendStatusActionsReplyKeyboardAsync(context);
        }

        return true;
    }

    private async Task<bool> HandleDeleteSessionAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var token = context.ParsedCallback.Argument;
        if (!int.TryParse(token, out var sessionId) || !sessionId.IsValidId())
        {
            LogInvalidInput("session ID", token, context.Username, context.UserId);
            return true;
        }

        Logger.LogDebug("User {Username} ({UserId}) deleting session {SessionId}", context.Username, context.UserId, sessionId);

        if (!await _dataService.DeleteSessionAsync(sessionId, context.UserId))
        {
            return true;
        }

        context.Session.IsInStatusView = true;
        await ShowSessionsListAsync(context);

        return true;
    }

    private async Task<bool> HandleDeleteCommandAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var token = context.ParsedCallback.Argument;
        if (!int.TryParse(token, out var commandId) || !commandId.IsValidId())
        {
            LogInvalidInput("command ID", token, context.Username, context.UserId);
            return true;
        }

        Logger.LogDebug("User {Username} ({UserId}) deleting command {CommandId}", context.Username, context.UserId, commandId);

        var sessionId = await _dataService.GetSessionIdByCommandAsync(commandId, context.UserId);
        if (!sessionId.HasValue)
        {
            Logger.LogWarning("User {Username} ({UserId}) attempted to access foreign or missing command {CommandId}", context.Username, context.UserId, commandId);
            return true;
        }

        if (!await _dataService.DeleteCommandAsync(commandId, context.UserId))
        {
            return true;
        }

        context.Session.SessionId = sessionId.Value;

        if (context.Buttons != null)
        {
            foreach (var row in context.Buttons)
            {
                _=row.RemoveAll(btn => btn.CallbackData!.Contains($"{commandId}"));
            }
        }

        var newKeyboard = ConvertDtoToKeyboard(context.Buttons);

        if (!await _dataService.CheckCommandsStatusAsync(sessionId.Value, context.UserId))
        {
            if (await _dataService.DeleteSessionAsync(sessionId.Value, context.UserId))
            {
                context.Session.IsInStatusView = true;
                await ShowSessionsListAsync(context);
            }
        }
        else
        {
            var sessionStatus = await _dataService.GetSessionsStatusAsync(sessionId.Value, context.UserId);
            await _outputService.EditMessageTextWithKeyboardAsync(context.UserId, context.MessageId, BuildStatusReply(sessionStatus), newKeyboard);
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
        var token = context.ParsedCallback.Argument;
        if (!int.TryParse(token, out var commandId) || !commandId.IsValidId())
        {
            LogInvalidInput("command ID", token, context.Username, context.UserId);
            return true;
        }

        Logger.LogDebug("User {Username} ({UserId}) initiating cancel for command {CommandId}", context.Username, context.UserId, commandId);

        // Проверяем, принадлежит ли команда пользователю
        var command = await _dataService.GetCommandByIdAsync(commandId, context.UserId);
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

        await _outputService.EditMessageTextWithKeyboardAsync(
            context.UserId,
            context.MessageId,
            $"❓ Вы уверены, что хотите отменить команду *#{command.CommandId} ({command.CommandText})*?",
            confirmKeyboard);

        return true;
    }

    private async Task<bool> HandleConfirmCancelAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var token = context.ParsedCallback.Argument;
        if (!int.TryParse(token, out var commandId) || !commandId.IsValidId())
        {
            LogInvalidInput("command ID", token, context.Username, context.UserId);
            return true;
        }

        Logger.LogDebug("User {Username} ({UserId}) confirmed cancel for command {CommandId}", context.Username, context.UserId, commandId);

        // Проверяем, принадлежит ли команда пользователю
        var command = await _dataService.GetCommandByIdAsync(commandId, context.UserId);
        if (command == null)
        {
            Logger.LogWarning("User {Username} ({UserId}) attempted to cancel foreign/missing command {CommandId}",
                context.Username, context.UserId, commandId);
            return true;
        }

        // Обновляем статус в БД на Cancelled
        var cancelled = await _dataService.CancelCommandAsync(commandId, context.UserId);
        if (!cancelled)
        {
            Logger.LogWarning("Cancel failed or command already finished: commandId={CommandId}", commandId);
            await _outputService.SendMessageAsync(context.UserId,
                "⛔ Не удалось отменить команду (возможно, она уже завершена).");
            return true;
        }

        // Уведомляем Worker о необходимости принудительно завершить процесс
        await _dataService.NotifyCommandCancelAsync(commandId);

        Logger.LogInformation("Command cancelled: commandId={CommandId}, userId={UserId}",
            commandId, context.UserId);

        // Уведомляем пользователя
        await _outputService.SendMessageAsync(context.UserId,
            $"⛔ Команда #{commandId} ({command.CommandText}) отменена.");

        // Обновляем отображение сессии
        context.Session.SessionId = command.SessionId;
        var sessionCommands = await _dataService.GetSessionsCommandsAsync(command.SessionId, context.UserId);
        var keyboard = await _keyboardBuilder.GetSessionCommandsKeyboardAsync(sessionCommands, command.SessionId);

        // Проверяем, остались ли ещё активные команды в сессии
        var hasActive = sessionCommands.Any(c =>
            c.Status == "pending" || c.Status == "processing");

        if (!hasActive)
        {
            // Если активных команд не осталось — показываем статус сессии
            var sessionStatus = await _dataService.GetSessionsStatusAsync(command.SessionId, context.UserId);
            await _outputService.EditMessageTextWithKeyboardAsync(
                context.UserId, context.MessageId, BuildStatusReply(sessionStatus), keyboard);
        }
        else
        {
            await _outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);
        }

        return true;
    }

    private async Task ShowSessionsListAsync(CallbackContext context)
    {
        var sessionsStatus = await _dataService.GetSessionsListAsync(context.UserId);
        var keyboard = await _keyboardBuilder.GetSessionsListKeyboardAsync(sessionsStatus);
        await _outputService.EditMessageTextWithKeyboardAsync(context.UserId, context.MessageId, "Сессии:", keyboard);
        context.Session.StatusMessageId = context.MessageId;

        var clearKeyboardMessage = await _outputService.RemoveReplyKeyboardAsync(context.UserId, "Сессии:");
        if (clearKeyboardMessage != null)
        {
            context.Session.TrackMessage(clearKeyboardMessage.Id);
        }
    }

    private async Task SendStatusActionsReplyKeyboardAsync(CallbackContext context)
    {
        var replyKeyboard = await _keyboardBuilder.GetStatusActionsReplyKeyboardAsync();
        var message = await _outputService.SendMessageWithReplyKeyboardAsync(context.UserId, "Действия:", replyKeyboard);
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

    private static InlineKeyboardMarkup ConvertDtoToKeyboard(List<List<ButtonDto>>? dto)
    {
        if (dto == null)
        {
            return new InlineKeyboardMarkup(Array.Empty<InlineKeyboardButton[]>());
        }

        var inlineKeyboard = dto
            .Select(row => row
                .Select(btn => InlineKeyboardButton.WithCallbackData(
                    btn.Text ?? "", btn.CallbackData ?? ""))
                .ToArray())
            .ToArray();

        return new InlineKeyboardMarkup(inlineKeyboard);
    }
}
