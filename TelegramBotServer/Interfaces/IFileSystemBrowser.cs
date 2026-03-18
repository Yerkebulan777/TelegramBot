using Telegram.Bot.Types.ReplyMarkups;

namespace TelegramBotServer.Interfaces
{
    public interface IFileSystemBrowser
    {
        /// <summary>Возвращает полный путь по токену.</summary>
        bool TryResolvePath(long userId, string token, out string? path);
        /// <summary>Возвращает представление файлов в директории.</summary>
        Task<(string message, InlineKeyboardMarkup keyboard)> GetFilesViewAsync(long userId, string path);
        /// <summary>Возвращает представление разделов.</summary>
        Task<(string message, InlineKeyboardMarkup keyboard)> GetSectionsViewAsync(long userId, string path);
        /// <summary>Возвращает представление проектов.</summary>
        Task<(string message, InlineKeyboardMarkup keyboard)> GetProjectsViewAsync(long userId, string path);
    }
}
