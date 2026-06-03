using Telegram.Bot.Types.ReplyMarkups;

namespace TelegramBot.Server.Interfaces;

public interface IFileSystemBrowser
{
    bool TryResolvePath(long userId, string token, out string? path);
    Task<InlineKeyboardMarkup> GetSectionsViewAsync(long userId, string path);
}
