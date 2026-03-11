using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotServer.Models;

namespace TelegramBotServer.Interfaces
{
    public interface IKeyboardBuilder
    {
        Task<InlineKeyboardMarkup> GetFileSelKeyboardAsync(long userId, UserSession session);
        Task<InlineKeyboardMarkup> GetSectionSelKeyboardAsync(long userId, UserSession session);
        Task<InlineKeyboardMarkup> GetProjectSelKeyboardAsync(long userId, UserSession session);
        Task<InlineKeyboardMarkup> GetSelectionKeyboardAsync(long userId, UserSession session);
        Task<InlineKeyboardMarkup> GetCommandsKeyboardAsync(long userId, UserSession session);
        Task<InlineKeyboardMarkup> GetSessionsListKeyboardAsync(List<SessionsList> sessionsStatus);
        Task<InlineKeyboardMarkup> GetSessionStatusKeyboardAsync(SessionStatus sessionStatus, int sessionId);
        Task<InlineKeyboardMarkup> GetSessionCommandsKeyboardAsync(List<SessionCommands> sessionCommands, int sessionId);
        Task<InlineKeyboardMarkup> GetAutomationKeyboardAsync(long userId, UserSession session);
    }
}
