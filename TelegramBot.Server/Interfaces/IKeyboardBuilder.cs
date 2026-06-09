using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Models;
using TelegramBot.Server.Models;

namespace TelegramBot.Server.Interfaces;

public interface IKeyboardBuilder
{
    Task<InlineKeyboardMarkup> GetSelectionKeyboardAsync(long userId, UserSession session);
    Task<InlineKeyboardMarkup> GetCommandKeyboardAsync(CommandGroup group, UserSession session);
    Task<ReplyKeyboardMarkup> GetCommandActionsReplyKeyboardAsync();
    Task<ReplyKeyboardMarkup> GetProjectActionsReplyKeyboardAsync();
    Task<ReplyKeyboardMarkup> GetSectionActionsReplyKeyboardAsync();
    Task<InlineKeyboardMarkup> GetSessionsListKeyboardAsync(List<SessionsList> sessionsStatus);
    Task<InlineKeyboardMarkup> GetSessionStatusKeyboardAsync(SessionStatus sessionStatus, int sessionId);
    Task<InlineKeyboardMarkup> GetSessionCommandsKeyboardAsync(List<SessionCommands> sessionCommands, int sessionId, string selectedFilter);
}
