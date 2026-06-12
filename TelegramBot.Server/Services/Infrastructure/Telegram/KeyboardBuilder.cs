using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Server.Models;
using TelegramBot.Server.Services.Infrastructure.FileSystem;

namespace TelegramBot.Server.Services.Infrastructure.Telegram;

public class KeyboardBuilder(FileSystemBrowser fileNavigationService)
{
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

    /// <summary>Строит клавиатуру списка сессий с фильтрами.</summary>
    public Task<InlineKeyboardMarkup> GetSessionsListKeyboardAsync(
        List<SessionsList> sessionsList, string currentFilter)
    {
        var buttons = new List<List<InlineKeyboardButton>>
        {
            StatusFilters.AllDescriptors
                .Select(f => InlineKeyboardButton.WithCallbackData(
                    FilterLabel(f.Title, currentFilter, f.Key),
                    $"{CallbackPrefixes.StatusFilter}{f.Key}"))
                .ToList()
        };

        foreach (var session in sessionsList)
        {
            var finished = session.DoneCommands + session.FailedCommands == session.TotalCommands;
            var progressIcon = finished ? "✅" : "🔄";
            var errorInfo = session.FailedCommands > 0 ? $" ⚠️{session.FailedCommands}" : "";
            var projectName = string.IsNullOrEmpty(session.ProjectName) ? "" : $" {session.ProjectName}";

            buttons.Add(
            [
                InlineKeyboardButton.WithCallbackData(
                    $"{projectName} 👤{session.Username} 📅{session.Date:dd.MM.yy} ({session.DoneCommands}/{session.TotalCommands}){progressIcon}{errorInfo}",
                    $"{CallbackPrefixes.SessionDetails}{session.SessionId}")
            ]);
        }

        return Task.FromResult(new InlineKeyboardMarkup(buttons));
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

    public Task<InlineKeyboardMarkup> GetSessionCommandsKeyboardAsync(List<SessionCommands> sessionCommands, int sessionId, string selectedFilter)
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

        foreach (var sessionCommand in visibleCommands)
        {
            var statusIcon = GetCommandStatusIcon(sessionCommand.Status);
            var fileName = Path.GetFileName(sessionCommand.FileName);

            if (sessionCommand.Status == "pending")
            {
                buttons.Add(
                [
                    InlineKeyboardButton.WithCallbackData(
                        $"{statusIcon} {sessionCommand.Command}: {fileName} ✖️",
                        $"{CallbackPrefixes.DeleteCommand}{sessionCommand.CommandId}:{selectedFilter}")
                ]);
            }
            else
            {
                buttons.Add(
                [
                    InlineKeyboardButton.WithCallbackData(
                        $"{statusIcon} {sessionCommand.Command}: {fileName}",
                        $"{CallbackPrefixes.SessionDetails}{sessionId}:{selectedFilter}")
                ]);
            }
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
