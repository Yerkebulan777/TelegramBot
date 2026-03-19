using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Models;

namespace TelegramBot.Server.Interfaces;

/// <summary>
/// Builds Telegram keyboards for various bot interactions.
/// </summary>
public interface IKeyboardBuilder
{
    /// <summary>Возвращает клавиатуру выбора файлов.</summary>
    Task<InlineKeyboardMarkup> GetSelectionKeyboardAsync(long userId, UserSession session);
    /// <summary>Возвращает клавиатуру экспортных команд.</summary>
    Task<InlineKeyboardMarkup> GetCommandsKeyboardAsync(UserSession session);
    /// <summary>Возвращает reply-клавиатуру для экспорта.</summary>
    Task<ReplyKeyboardMarkup> GetExportActionsReplyKeyboardAsync();
    /// <summary>Возвращает reply-клавиатуру для автоматизации.</summary>
    Task<ReplyKeyboardMarkup> GetAutomationActionsReplyKeyboardAsync();
    /// <summary>Возвращает reply-клавиатуру для файлов.</summary>
    Task<ReplyKeyboardMarkup> GetFileActionsReplyKeyboardAsync(UserSession session);
    /// <summary>Возвращает клавиатуру списка сессий.</summary>
    Task<InlineKeyboardMarkup> GetSessionsListKeyboardAsync(List<SessionsList> sessionsStatus);
    /// <summary>Возвращает клавиатуру статуса сессии.</summary>
    Task<InlineKeyboardMarkup> GetSessionStatusKeyboardAsync(SessionStatus sessionStatus, int sessionId);
    /// <summary>Возвращает клавиатуру команд сессии.</summary>
    Task<InlineKeyboardMarkup> GetSessionCommandsKeyboardAsync(List<SessionCommands> sessionCommands, int sessionId);
    /// <summary>Возвращает клавиатуру автоматизации.</summary>
    Task<InlineKeyboardMarkup> GetAutomationKeyboardAsync(UserSession session);
}
