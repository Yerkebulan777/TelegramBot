using Telegram.Bot.Types.ReplyMarkups;

namespace TelegramBotServer.Interfaces
{
    public interface IFileSystemBrowser
    {
        Task<(string message, InlineKeyboardMarkup keyboard)> GetFilesViewAsync(long userId, string path);
        bool IsFile(string path);
        bool TryResolvePath(long userId, string token, out string? path);


        Task<(string message, InlineKeyboardMarkup keyboard)> GetSectionsViewAsync(long userId, string path);
        Task<(string message, InlineKeyboardMarkup keyboard)> GetProjectsViewAsync(long userId, string path);
    }
}
