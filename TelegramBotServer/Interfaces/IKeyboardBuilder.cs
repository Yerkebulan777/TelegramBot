using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotServer.Models;

namespace TelegramBotServer.Interfaces
{
    public interface IKeyboardBuilder
    {
        Task<InlineKeyboardMarkup> GetSelectionKeyboardAsync(long userId, UserSession session);
        Task<InlineKeyboardMarkup> GetCommandsKeyboardAsync(UserSession session);
        Task<ReplyKeyboardMarkup> GetExportActionsReplyKeyboardAsync();
        Task<ReplyKeyboardMarkup> GetAutomationActionsReplyKeyboardAsync();
        Task<InlineKeyboardMarkup> GetSessionsListKeyboardAsync(List<SessionsList> sessionsStatus);
        Task<InlineKeyboardMarkup> GetSessionStatusKeyboardAsync(SessionStatus sessionStatus, int sessionId);
        Task<InlineKeyboardMarkup> GetSessionCommandsKeyboardAsync(List<SessionCommands> sessionCommands, int sessionId);
        Task<InlineKeyboardMarkup> GetAutomationKeyboardAsync(UserSession session);
    }
}
