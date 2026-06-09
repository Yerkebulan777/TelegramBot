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
            var summary = session.DoneCommands + session.FailedCommands == session.TotalCommands
                ? $"{session.DoneCommands}/{session.TotalCommands} ✅"
                : $"{session.DoneCommands}/{session.TotalCommands} ({session.ActiveCommands} актив.)";

            if (session.FailedCommands > 0)
            {
                summary += $" ❌{session.FailedCommands}";
            }

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

    public Task<InlineKeyboardMarkup> GetSessionCommandsKeyboardAsync(List<SessionCommands> sessionCommands, int sessionId)
    {
        var buttons = new List<List<InlineKeyboardButton>>
        {
            new()
            {
                InlineKeyboardButton.WithCallbackData("🗑 Удалить всё", $"{CallbackPrefixes.DeleteSession}{sessionId}")
            }
        };

        foreach (var sessionCommand in sessionCommands)
        {
            var statusIcon = GetCommandStatusIcon(sessionCommand.Status);
            var fileName = Path.GetFileName(sessionCommand.FileName);

            // Row 1: order + command type + status
            buttons.Add(
            [
                InlineKeyboardButton.WithCallbackData(
                    $"{sessionCommand.ExecOrder}. {sessionCommand.Command} {statusIcon}",
                    $"{sessionCommand.CommandId}"),
            ]);

            // Row 2: filename + action buttons
            var actionButtons = new List<InlineKeyboardButton>
            {
                InlineKeyboardButton.WithCallbackData(
                    $"📄 {fileName}",
                    $"{sessionCommand.CommandId}")
            };

            if (sessionCommand.Status == "processing")
            {
                actionButtons.Add(
                    InlineKeyboardButton.WithCallbackData("⛔ Отменить", $"{CallbackPrefixes.DeleteCommand}{sessionCommand.CommandId}"));
            }

            if (sessionCommand.Status != "processing")
            {
                actionButtons.Add(
                    InlineKeyboardButton.WithCallbackData("🗑", $"{CallbackPrefixes.DeleteCommand}{sessionCommand.CommandId}"));
            }

            buttons.Add(actionButtons);
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
