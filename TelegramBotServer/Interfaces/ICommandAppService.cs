using TelegramBotServer.DTOs;

namespace TelegramBotServer.Interfaces
{
    public interface ICommandAppService
    {
        Task HandleUserCommandAsync(MessageDto message);

        Task HandleCallbackAsync(CallbackQueryDto callback);

        //Task HandleSystemNotificationAsync(SystemNotificationDto dto);

    }
}
