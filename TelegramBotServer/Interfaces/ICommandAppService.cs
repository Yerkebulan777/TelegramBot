using TelegramBotServer.DTOs;

namespace TelegramBotServer.Interfaces
{
    public interface ICommandAppService
    {
        /// <summary>
        /// Handles a user message command.
        /// </summary>
        /// <param name="message">The message DTO.</param>
        /// <param name="cancellationToken">Token for cancellation.</param>
        Task HandleUserCommandAsync(MessageDto message, CancellationToken cancellationToken = default);

        /// <summary>
        /// Handles a callback query from inline keyboard.
        /// </summary>
        /// <param name="callback">The callback DTO.</param>
        /// <param name="cancellationToken">Token for cancellation.</param>
        Task HandleCallbackAsync(CallbackQueryDto callback, CancellationToken cancellationToken = default);
    }
}
