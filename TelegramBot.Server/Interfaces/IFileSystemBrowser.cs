using Telegram.Bot.Types.ReplyMarkups;

namespace TelegramBot.Server.Interfaces;

/// <summary>
/// Browses the filesystem and builds inline keyboard representations.
/// </summary>
public interface IFileSystemBrowser
{
    /// <summary>Возвращает полный путь по токену.</summary>
    bool TryResolvePath(long userId, string token, out string? path);

    /// <summary>Возвращает клавиатуру со списком разделов текущей директории.</summary>
    Task<InlineKeyboardMarkup> GetSectionsViewAsync(long userId, string path);
}
