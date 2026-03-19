using TelegramBot.Core.DTOs;

namespace TelegramBot.Core.Interfaces;

/// <summary>
/// Обрабатывает входящие команды и callback-запросы от пользователей.
/// </summary>
public interface ICommandAppService
{
    /// <summary>
    /// Обрабатывает входящее текстовое сообщение от пользователя.
    /// </summary>
    Task HandleUserCommandAsync(MessageDto message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Обрабатывает callback-запрос от inline-кнопки.
    /// </summary>
    Task HandleCallbackAsync(CallbackQueryDto callback, CancellationToken cancellationToken = default);
}
