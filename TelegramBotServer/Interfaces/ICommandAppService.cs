using TelegramBotServer.DTOs;

namespace TelegramBotServer.Interfaces
{
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
}
