using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Server.Models;
using TelegramBot.Server.Services.Infrastructure.FileSystem;

namespace TelegramBot.Server.Services.Infrastructure.Telegram;

public class KeyboardBuilder(FileSystemBrowser fileNavigationService)
{
    /// <summary>
    /// Размер страницы списков в /status. Длинные подписи кнопок обрезаются,
    /// чтобы не превышать лимит Telegram reply_markup (~4096 байт).
    /// </summary>
    public const int SessionsPageSize = 15;
    private const int MaxListButtonTextLength = 64;

    public Task<InlineKeyboardMarkup> GetSelectionKeyboardAsync(long userId, UserSession session)
    {
        return fileNavigationService.GetSectionsViewAsync(userId, session.CurrentPath);
    }

    public Task<InlineKeyboardMarkup> GetCommandKeyboardAsync(CommandGroup group, UserSession session)
    {
        return Task.FromResult(BuildSelectableCommandsKeyboard(session, CommandCatalog.GetByGroup(group)));
    }

    public Task<ReplyKeyboardMarkup> GetCommandActionsReplyKeyboardAsync()
    {
        return Task.FromResult(BuildActionsReplyKeyboard(ButtonTexts.Apply, ButtonTexts.Cancel));
    }

    /// <summary>
    /// Reply-клавиатура действий при выборе файлов/разделов (Confirm + Cancel).
    /// Используется и на уровне проекта, и на уровне разделов — набор кнопок идентичен.
    /// </summary>
    public Task<ReplyKeyboardMarkup> GetFileActionsReplyKeyboardAsync()
    {
        return Task.FromResult(BuildActionsReplyKeyboard(ButtonTexts.Confirm, ButtonTexts.Cancel));
    }

    /// <summary>Строит клавиатуру списка сессий с фильтрами и постраничной навигацией.</summary>
    /// <param name="page">Запрошенная страница (0-based). Клампится в валидный диапазон.</param>
    public Task<InlineKeyboardMarkup> GetSessionsListKeyboardAsync(
        List<SessionsList> sessionsList, string currentFilter, int page = 0)
    {
        var total = sessionsList.Count;
        var totalPages = total > 0
            ? (total + SessionsPageSize - 1) / SessionsPageSize
            : 0;
        var clampedPage = totalPages > 0
            ? Math.Clamp(page, 0, totalPages - 1)
            : 0;

        var buttons = new List<List<InlineKeyboardButton>>
        {
            StatusFilters.AllDescriptors
                .Select(f => InlineKeyboardButton.WithCallbackData(
                    FilterLabel(f.Title, currentFilter, f.Key),
                    $"{CallbackPrefixes.StatusFilter}{f.Key}"))
                .ToList()
        };

        foreach (var session in sessionsList.Skip(clampedPage * SessionsPageSize).Take(SessionsPageSize))
        {
            var finished = session.DoneCommands + session.FailedCommands == session.TotalCommands;
            var progressIcon = finished ? "✅" : "🔄";
            var errorInfo = session.FailedCommands > 0 ? $" ⚠️{session.FailedCommands}" : "";
            var projectName = string.IsNullOrEmpty(session.ProjectName) ? "" : $" {session.ProjectName}";

            var label = $"{projectName} 👤{session.Username} 📅{session.Date:dd.MM.yy} ({session.DoneCommands}/{session.TotalCommands}){progressIcon}{errorInfo}";
            buttons.Add(
            [
                InlineKeyboardButton.WithCallbackData(
                    TruncateListButtonText(label),
                    $"{CallbackPrefixes.SessionDetails}{session.SessionId}")
            ]);
        }

        if (totalPages > 1)
        {
            var navRow = new List<InlineKeyboardButton>();
            if (clampedPage > 0)
            {
                navRow.Add(InlineKeyboardButton.WithCallbackData(
                    "◀️ Назад", $"{CallbackPrefixes.StatusPage}{clampedPage - 1}"));
            }
            if (clampedPage < totalPages - 1)
            {
                navRow.Add(InlineKeyboardButton.WithCallbackData(
                    "Вперёд ▶️", $"{CallbackPrefixes.StatusPage}{clampedPage + 1}"));
            }
            if (navRow.Count > 0)
            {
                buttons.Add(navRow);
            }
        }

        return Task.FromResult(new InlineKeyboardMarkup(buttons));
    }

    /// <summary>Вычисляет (clampedPage, totalPages) для отображения в тексте сообщения /status.</summary>
    public static (int ClampedPage, int TotalPages) GetSessionsPageInfo(int totalCount, int page)
    {
        var totalPages = totalCount > 0
            ? (totalCount + SessionsPageSize - 1) / SessionsPageSize
            : 0;
        var clampedPage = totalPages > 0
            ? Math.Clamp(page, 0, totalPages - 1)
            : 0;
        return (clampedPage, totalPages);
    }

    private static string FilterLabel(string title, string currentFilter, string filterKey) =>
        string.Equals(filterKey, currentFilter, StringComparison.OrdinalIgnoreCase)
            ? $"🔹 {title}"
            : title;

    public Task<InlineKeyboardMarkup> GetSessionStatusKeyboardAsync(SessionStatus sessionStatus, int sessionId)
    {
        var buttons = new List<List<InlineKeyboardButton>>
        {
            new()
            {
                InlineKeyboardButton.WithCallbackData("📋 Команды", $"{CallbackPrefixes.SessionDetails}{sessionId}"),
                InlineKeyboardButton.WithCallbackData("🗑 Удалить", $"{CallbackPrefixes.DeleteSession}{sessionId}")
            }
        };
        return Task.FromResult(new InlineKeyboardMarkup(buttons));
    }

    public Task<InlineKeyboardMarkup> GetSessionCommandsKeyboardAsync(
        List<SessionCommands> sessionCommands, int sessionId, string selectedFilter, int page = 0)
    {
        var buttons = new List<List<InlineKeyboardButton>>();

        var tabRow = new List<InlineKeyboardButton>();

        var uniqueCommands = sessionCommands
            .Select(c => c.Command)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c)
            .ToList();

        foreach (var cmdType in uniqueCommands)
        {
            var count = sessionCommands.Count(c => string.Equals(c.Command, cmdType, StringComparison.OrdinalIgnoreCase));
            var isSelected = string.Equals(selectedFilter, cmdType, StringComparison.OrdinalIgnoreCase);
            var label = isSelected ? $"🔹 {cmdType} ({count})" : $"{cmdType} ({count})";
            var nextFilter = isSelected ? "SUMMARY" : cmdType;

            tabRow.Add(InlineKeyboardButton.WithCallbackData(
                label,
                $"{CallbackPrefixes.SessionDetails}{sessionId}:{nextFilter}"));
        }

        buttons.Add(tabRow);

        var isFiltered = !string.IsNullOrEmpty(selectedFilter)
            && !string.Equals(selectedFilter, "ALL", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(selectedFilter, "SUMMARY", StringComparison.OrdinalIgnoreCase);

        var deleteAllCallback = isFiltered
            ? $"{CallbackPrefixes.DeleteSessionByType}{sessionId}:{selectedFilter}"
            : $"{CallbackPrefixes.DeleteSession}{sessionId}";

        var deleteAllLabel = isFiltered ? $"🗑 Удалить все ({selectedFilter})" : "🗑 Удалить всё";

        buttons.Add(
        [
            InlineKeyboardButton.WithCallbackData(deleteAllLabel, deleteAllCallback)
        ]);

        var visibleCommands = string.IsNullOrEmpty(selectedFilter)
            || string.Equals(selectedFilter, "ALL", StringComparison.OrdinalIgnoreCase)
            || string.Equals(selectedFilter, "SUMMARY", StringComparison.OrdinalIgnoreCase)
            ? sessionCommands
            : sessionCommands.Where(c => string.Equals(c.Command, selectedFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        var totalFiles = visibleCommands.Count;
        var totalFilePages = totalFiles > 0
            ? (totalFiles + SessionsPageSize - 1) / SessionsPageSize
            : 0;
        var clampedFilePage = totalFilePages > 0
            ? Math.Clamp(page, 0, totalFilePages - 1)
            : 0;

        foreach (var sessionCommand in visibleCommands.Skip(clampedFilePage * SessionsPageSize).Take(SessionsPageSize))
        {
            var statusIcon = GetCommandStatusIcon(sessionCommand.Status);
            var fileName = Path.GetFileName(sessionCommand.FileName);

            if (sessionCommand.Status == "pending")
            {
                var label = $"{statusIcon} {sessionCommand.Command}: {fileName} ✖️";
                buttons.Add(
                [
                    InlineKeyboardButton.WithCallbackData(
                        TruncateListButtonText(label),
                        $"{CallbackPrefixes.DeleteCommand}{sessionCommand.CommandId}:{selectedFilter}")
                ]);
            }
            else
            {
                var label = $"{statusIcon} {sessionCommand.Command}: {fileName}";
                buttons.Add(
                [
                    InlineKeyboardButton.WithCallbackData(
                        TruncateListButtonText(label),
                        $"{CallbackPrefixes.SessionDetails}{sessionId}:{selectedFilter}")
                ]);
            }
        }

        if (totalFilePages > 1)
        {
            var navRow = new List<InlineKeyboardButton>();
            if (clampedFilePage > 0)
            {
                navRow.Add(InlineKeyboardButton.WithCallbackData(
                    "◀️ Назад", $"{CallbackPrefixes.CommandsPage}{sessionId}:{selectedFilter}:{clampedFilePage - 1}"));
            }
            if (clampedFilePage < totalFilePages - 1)
            {
                navRow.Add(InlineKeyboardButton.WithCallbackData(
                    "Вперёд ▶️", $"{CallbackPrefixes.CommandsPage}{sessionId}:{selectedFilter}:{clampedFilePage + 1}"));
            }
            buttons.Add(navRow);
        }

        return Task.FromResult(new InlineKeyboardMarkup(buttons));
    }

    private static string GetCommandStatusIcon(string status)
    {
        return status switch
        {
            "pending" => "⏳",
            "processing" => "🔄",
            "Done" => "✅",
            "Failed" => "❌",
            "Deleted" => "🗑",
            _ => "❓"
        };
    }

    private static string TruncateListButtonText(string text)
    {
        const string suffix = "...";
        return text.Length <= MaxListButtonTextLength
            ? text
            : text[..(MaxListButtonTextLength - suffix.Length)] + suffix;
    }

    private static InlineKeyboardMarkup BuildSelectableCommandsKeyboard(
        UserSession session, IEnumerable<CommandDefinition> commandOptions)
    {
        var buttons = commandOptions
            .Select(option =>
            {
                var isSelected = session.PendingCommand.Contains(option.Code);
                var text = isSelected ? $"✅ {option.Name}" : option.Name;
                return new List<InlineKeyboardButton>
                {
                    InlineKeyboardButton.WithCallbackData(text, option.Prefix)
                };
            })
            .ToList();

        return new InlineKeyboardMarkup(buttons);
    }

    private static ReplyKeyboardMarkup BuildActionsReplyKeyboard(params string[] buttonTexts)
    {
        return new ReplyKeyboardMarkup(
        [
            buttonTexts.Select(text => new KeyboardButton(text)).ToArray()
        ])
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = false
        };
    }
}
