using Telegram.Bot.Types.ReplyMarkups;

namespace TelegramBot.Server.Interfaces;

public interface IFileSystemBrowser
{
    Task<InlineKeyboardMarkup> GetSectionsViewAsync(long userId, string path);
}
