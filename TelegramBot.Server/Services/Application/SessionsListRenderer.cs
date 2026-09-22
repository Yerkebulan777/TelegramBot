using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Data;
using TelegramBot.Server.Helpers;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Services.Application;

/// <summary>
/// Рендерит список сессий /status: загрузка списка, шапка с фильтром, отправка или редактирование.
/// Используется из <see cref="SlashCommandService"/> (исходный /status) и <see cref="Handlers.SessionManagementHandler"/> (фильтры/возврат).
/// </summary>
public sealed class SessionsListRenderer(
    SessionDataService sessionDataService,
    KeyboardBuilder keyboardBuilder,
    TelegramOutputService outputService,
    ILogger<SessionsListRenderer> logger)
{
    /// <summary>Строит текст сообщения и клавиатуру для текущего фильтра.</summary>
    private async Task<(string Text, InlineKeyboardMarkup Keyboard)> BuildAsync(
        long userId, string filter, CancellationToken cancellationToken = default)
    {
        var sessions = await sessionDataService.GetSessionsListFilteredAsync(userId, filter);
        var text = $"{StatusFilters.GetTitle(filter)} (всего {sessions.Count})";
        var keyboard = keyboardBuilder.GetSessionsListKeyboard(sessions, filter);
        return (text, keyboard);
    }

    /// <summary>Отправляет список как новое сообщение (первый запуск /status).</summary>
    public async Task<Message?> SendNewAsync(long userId, string filter, CancellationToken cancellationToken = default)
    {
        var (text, keyboard) = await BuildAsync(userId, filter, cancellationToken);
        return await outputService.SendMessageWithKeyboardAsync(userId, text, keyboard, cancellationToken);
    }

    /// <summary>Редактирует существующее сообщение /status (переключение фильтра, возврат после удаления).</summary>
    public async Task EditExistingAsync(
        long userId, int targetMessageId, string filter, string username, CancellationToken cancellationToken = default)
    {
        var (text, keyboard) = await BuildAsync(userId, filter, cancellationToken);
        logger.LogInformation("{Username} view sessions: filter={Filter}", username, filter);
        await outputService.EditMessageTextWithKeyboardAsync(userId, targetMessageId, text, keyboard);
    }

    /// <summary>
    /// Строит текст статуса одной сессии: шапка (проект/дата) + сводка по группам команд.
    /// Вынесено из SessionManagementHandler — presentation-логика централизована в рендерере.
    /// </summary>
    public static string BuildStatusReply(SessionStatus sessionStatus, List<SessionCommands>? sessionCommands = null)
    {
        var projectName = MarkdownHelper.Escape(sessionStatus.ProjectName!);

        if (sessionCommands == null || sessionCommands.Count == 0)
        {
            return $"*{projectName}*\n  📅 {sessionStatus.CreatedAt:dd.MM.yyyy · HH:mm}";
        }

        var statusIcon = sessionStatus.Status switch
        {
            Statuses.Done => "✅",
            Statuses.Failed => "❌",
            Statuses.Deleted => "🗑",
            _ => "🔄"
        };

        var header = $"{statusIcon} *{projectName}*\n  📅 {sessionStatus.CreatedAt:dd.MM.yyyy · HH:mm}";

        var commandLines = sessionCommands
            .GroupBy(c => c.Command)
            .OrderBy(g => g.Key)
            .Select(group =>
            {
                var totalInGroup = group.Count();
                var doneInGroup = group.Count(c => c.Status == Statuses.Done);
                var failedInGroup = group.Count(c => c.Status == Statuses.Failed);
                var processingInGroup = group.Count(c => c.Status == Statuses.Processing);
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
