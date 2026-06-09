using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Server.Interfaces;
using TelegramBot.Server.Models;
using TelegramBot.Server.Services.Infrastructure.FileSystem;

namespace TelegramBot.Server.Services.Infrastructure.Telegram;

public class KeyboardBuilder(FileSystemBrowser fileNavigationService) : IKeyboardBuilder
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

    public Task<ReplyKeyboardMarkup> GetProjectActionsReplyKeyboardAsync()
    {
        return Task.FromResult(BuildActionsReplyKeyboard(ButtonTexts.Confirm, ButtonTexts.Cancel));
    }

    public Task<ReplyKeyboardMarkup> GetSectionActionsReplyKeyboardAsync()
    {
        return Task.FromResult(BuildActionsReplyKeyboard(ButtonTexts.Confirm, ButtonTexts.Cancel));
    }

    public Task<InlineKeyboardMarkup> GetSessionsListKeyboardAsync(List<SessionsList> sessionsList)
    {
        var buttons = new List<List<InlineKeyboardButton>>();

        foreach (var session in sessionsList)
        {
            var statusIcon = GetSessionStatusIcon(session.Status, session.ActiveCommands);
            var projectName = string.IsNullOrEmpty(session.ProjectName) ? "" : $" {session.ProjectName}";

            var progressBar = session.DoneCommands + session.FailedCommands == session.TotalCommands
                ? "✅ завершено"
                : "🔄 выполнено";

            if (session.FailedCommands > 0)
            {
                progressBar += $" | ❌ ошибок: {session.FailedCommands}";
            }

            var summary = $"{session.DoneCommands}/{session.TotalCommands} {progressBar}";

            buttons.Add(
            [
                InlineKeyboardButton.WithCallbackData(
                    $"{statusIcon}{projectName} [{session.Username}] {session.Date:dd.MM.yy HH:mm} — {summary}",
                    $"{CallbackPrefixes.SessionDetails}{session.SessionId}")
            ]);
        }
        return Task.FromResult(new InlineKeyboardMarkup(buttons));
    }

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

    private static string GetSessionStatusIcon(string status, int activeCommands)
    {
        return activeCommands > 0
            ? "🔄"
            : status switch
            {
                "Done" => "✅",
                "Failed" => "❌",
                "Deleted" => "🗑",
                _ => "📋"
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
