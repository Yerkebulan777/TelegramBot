using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Constants;
using TelegramBot.Data;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Services.Application;

/// <summary>
/// Рендерит список сессий /status: параллельный fetch, шапка с фильтром, отправка или редактирование.
/// Используется из <see cref="SlashCommandService"/> (исходный /status) и <see cref="Handlers.SessionManagementHandler"/> (фильтры/возврат).
/// </summary>
public sealed class SessionsListRenderer(
    SessionDataService sessionDataService,
    KeyboardBuilder keyboardBuilder,
    TelegramOutputService outputService,
    ILogger<SessionsListRenderer> logger)
{
    /// <summary>Строит текст сообщения и клавиатуру для текущего фильтра и страницы.</summary>
    private async Task<(string Text, InlineKeyboardMarkup Keyboard)> BuildAsync(
        string filter, int page, CancellationToken cancellationToken = default)
    {
        var sessionsTask = sessionDataService.GetSessionsListFilteredAsync(filter);
        var countTask = sessionDataService.CountSessionsFilteredAsync(filter);
        await Task.WhenAll(sessionsTask, countTask);

        var sessions = await sessionsTask;
        var total = await countTask;
        var (clampedPage, totalPages) = KeyboardBuilder.GetSessionsPageInfo(total, page);

        var text = totalPages > 1
            ? $"{StatusFilters.GetTitle(filter)} (всего {total} • стр. {clampedPage + 1}/{totalPages})"
            : $"{StatusFilters.GetTitle(filter)} (всего {total})";

        var keyboard = keyboardBuilder.GetSessionsListKeyboard(sessions, filter, clampedPage);
        return (text, keyboard);
    }

    /// <summary>Отправляет список как новое сообщение (первый запуск /status).</summary>
    public async Task<Message?> SendNewAsync(long chatId, string filter, int page = 0, CancellationToken cancellationToken = default)
    {
        var (text, keyboard) = await BuildAsync(filter, page, cancellationToken);
        return await outputService.SendMessageWithKeyboardAsync(chatId, text, keyboard);
    }

    /// <summary>Редактирует существующее сообщение /status (переключение фильтра/страницы, возврат после удаления).</summary>
    public async Task EditExistingAsync(
        long chatId, int targetMessageId, string filter, int page, string username, CancellationToken cancellationToken = default)
    {
        var (text, keyboard) = await BuildAsync(filter, page, cancellationToken);
        logger.LogInformation("{Username} view sessions: filter={Filter}, page={Page}", username, filter, page);
        await outputService.EditMessageTextWithKeyboardAsync(chatId, targetMessageId, text, keyboard);
    }
}
