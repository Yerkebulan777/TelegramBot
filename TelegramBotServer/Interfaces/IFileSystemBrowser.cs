using Telegram.Bot.Types.ReplyMarkups;

namespace TelegramBotServer.Interfaces
{
    public interface IFileSystemBrowser
    {
        bool TryResolvePath(long userId, string token, out string? path);
        Task<(string message, InlineKeyboardMarkup keyboard)> GetFilesViewAsync(long userId, string path);
        Task<(string message, InlineKeyboardMarkup keyboard)> GetSectionsViewAsync(long userId, string path);
        Task<(string message, InlineKeyboardMarkup keyboard)> GetProjectsViewAsync(long userId, string path);
    }
}
