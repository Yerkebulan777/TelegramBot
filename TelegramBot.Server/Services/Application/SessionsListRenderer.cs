using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Constants;
using TelegramBot.Data;
using TelegramBot.Server.Interfaces;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Services.Application;

/// <summary>
/// Рендерит список сессий /status: параллельный fetch, шапка с фильтром, отправка или редактирование.
/// Используется из <see cref="SlashCommandService"/> (исходный /status) и <see cref="Handlers.SessionManagementHandler"/> (фильтры/возврат).
/// </summary>
public sealed class SessionsListRenderer(
    DataServices dataServices,
    KeyboardBuilder keyboardBuilder,
    ITelegramOutputService outputService,
    ILogger<SessionsListRenderer> logger)
{
    /// <summary>Строит текст сообщения и клавиатуру для текущего фильтра.</summary>
    private async Task<(string Text, InlineKeyboardMarkup Keyboard)> BuildAsync(
        string filter, CancellationToken cancellationToken = default)
    {
        var sessionsTask = dataServices.Sessions.GetSessionsListFilteredAsync(filter);
        var countTask = dataServices.Sessions.CountSessionsFilteredAsync(filter);
        await Task.WhenAll(sessionsTask, countTask);

        var text = $"{StatusFilters.GetTitle(filter)} (всего {await countTask})";
        var keyboard = await keyboardBuilder.GetSessionsListKeyboardAsync(await sessionsTask, filter);
        return (text, keyboard);
    }

    /// <summary>Отправляет список как новое сообщение (первый запуск /status).</summary>
    public async Task<Message?> SendNewAsync(long chatId, string filter, CancellationToken cancellationToken = default)
    {
        var (text, keyboard) = await BuildAsync(filter, cancellationToken);
        return await outputService.SendMessageWithKeyboardAsync(chatId, text, keyboard);
    }

    /// <summary>Редактирует существующее сообщение /status (переключение фильтра, возврат после удаления).</summary>
    public async Task EditExistingAsync(
        long chatId, int targetMessageId, string filter, string username, CancellationToken cancellationToken = default)
    {
        var (text, keyboard) = await BuildAsync(filter, cancellationToken);
        logger.LogInformation("{Username} view sessions — filter={Filter}", username, filter);
        await outputService.EditMessageTextWithKeyboardAsync(chatId, targetMessageId, text, keyboard);
    }
}
