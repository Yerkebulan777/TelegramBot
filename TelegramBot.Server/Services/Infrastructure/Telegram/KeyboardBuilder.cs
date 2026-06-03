using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Infrastructure.Telegram;

public readonly record struct CommandOption(string Text, string CallbackData, string CommandKey);

public class KeyboardBuilder(IFileSystemBrowser fileNavigationService) : IKeyboardBuilder
{
    private readonly IFileSystemBrowser _navigationService = fileNavigationService;

    public Task<InlineKeyboardMarkup> GetSelectionKeyboardAsync(long userId, UserSession session) =>
        _navigationService.GetSectionsViewAsync(userId, session.CurrentPath);

    public Task<InlineKeyboardMarkup> GetCommandsKeyboardAsync(UserSession session)
    {
        var commandOptions = new List<CommandOption>
        {
            new("Export to PDF", CallbackPrefixes.Pdf, CommandCodes.Pdf),
            new("Export to DWG", CallbackPrefixes.Dwg, CommandCodes.Dwg),
            new("Export to NWC", CallbackPrefixes.Nwc, CommandCodes.Nwc),
            new("Export to IFC", CallbackPrefixes.Ifc, CommandCodes.Ifc)
        };

        return Task.FromResult(BuildSelectableCommandsKeyboard(session, commandOptions));
    }

    public Task<ReplyKeyboardMarkup> GetExportActionsReplyKeyboardAsync()
        => Task.FromResult(BuildActionsReplyKeyboard(ButtonTexts.ExportApply));

    public Task<ReplyKeyboardMarkup> GetAutomationActionsReplyKeyboardAsync()
        => Task.FromResult(BuildActionsReplyKeyboard(ButtonTexts.AutomationApply));

    public Task<InlineKeyboardMarkup> GetSessionsListKeyboardAsync(List<SessionsList> sessionsList)
    {
        var buttons = new List<List<InlineKeyboardButton>>();

        foreach (SessionsList session in sessionsList)
        {
            buttons.Add(
            [
                InlineKeyboardButton.WithCallbackData(session.Date.ToString(), $"{CallbackPrefixes.SessionDetails}{session.SessionId}")
            ]);
        }
        return Task.FromResult(new InlineKeyboardMarkup(buttons));
    }

    public Task<InlineKeyboardMarkup> GetSessionStatusKeyboardAsync(SessionStatus sessionStatus, int sessionId)
    {
        _ = sessionStatus;

        var buttons = new List<List<InlineKeyboardButton>>
        {
            new()
            {
                InlineKeyboardButton.WithCallbackData("More", $"{CallbackPrefixes.SessionDetails}{sessionId}"),
                InlineKeyboardButton.WithCallbackData("Delete All", $"{CallbackPrefixes.DeleteSession}{sessionId}"),
                InlineKeyboardButton.WithCallbackData("Back", CallbackPrefixes.BackToStatus)
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
                InlineKeyboardButton.WithCallbackData("Less", $"{CallbackPrefixes.SessionDetails}{sessionId}"),
                InlineKeyboardButton.WithCallbackData("Delete All", $"{CallbackPrefixes.DeleteSession}{sessionId}"),
                InlineKeyboardButton.WithCallbackData("Back", CallbackPrefixes.BackToStatus)
            }
        };

        foreach (SessionCommands sessionCommand in sessionCommands)
        {
            buttons.Add(
            [
                InlineKeyboardButton.WithCallbackData($"{sessionCommand.ExecOrder} | {sessionCommand.Command} | {Path.GetFileName(sessionCommand.FileName)} | {sessionCommand.Status}", $"{sessionCommand.CommandId}"),
            ]);
            buttons.Add(
            [
                InlineKeyboardButton.WithCallbackData($"{sessionCommand.Date}", $"{sessionCommand.CommandId}"),
                InlineKeyboardButton.WithCallbackData("🗑 Delete", $"{CallbackPrefixes.DeleteCommand}{sessionCommand.CommandId}")
            ]);
        }

        return Task.FromResult(new InlineKeyboardMarkup(buttons));
    }

    public Task<InlineKeyboardMarkup> GetAutomationKeyboardAsync(UserSession session)
    {
        var commandOptions = new List<CommandOption>
        {
            new("BIM Doctor", CallbackPrefixes.BimDoc, CommandCodes.BimDoc),
            new("Clash Report", CallbackPrefixes.ClashRep, CommandCodes.ClashRep),
            new("Auto Resolver", CallbackPrefixes.AutoRes, CommandCodes.AutoRes)
        };

        return Task.FromResult(BuildSelectableCommandsKeyboard(session, commandOptions));
    }

    private static InlineKeyboardMarkup BuildSelectableCommandsKeyboard(
        UserSession session, List<CommandOption> commandOptions)
    {
        var buttons = commandOptions
            .Select(option =>
            {
                bool isSelected = session.PendingCommand.Contains(option.CommandKey);
                string text = isSelected ? $"✅ {option.Text}" : option.Text;
                return new List<InlineKeyboardButton>
                {
                    InlineKeyboardButton.WithCallbackData(text, option.CallbackData)
                };
            })
            .ToList();

        return new InlineKeyboardMarkup(buttons);
    }

    private static ReplyKeyboardMarkup BuildActionsReplyKeyboard(string applyButtonText)
    {
        return new ReplyKeyboardMarkup(
        [
            [new(applyButtonText), new(ButtonTexts.Cancel)]
        ])
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = false
        };
    }
}
