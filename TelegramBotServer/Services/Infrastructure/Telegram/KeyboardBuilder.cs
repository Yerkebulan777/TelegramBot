using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services.Infrastructure.Telegram;

public readonly record struct CommandOption(string Text, string CallbackData, string CommandKey);

/// <summary>
/// Стандартные тексты кнопок.
/// </summary>
public static class ButtonTexts
{
    public const string ExportApply = "✅ Применить";
    public const string AutomationApply = "✅ Подтвердить";
    public const string Cancel = "❌ Отмена";
}

/// <summary>
/// Построитель клавиатур для Telegram-бота.
/// </summary>
public class KeyboardBuilder(IFileSystemBrowser fileNavigationService) : IKeyboardBuilder
{
    private readonly IFileSystemBrowser _navigationService = fileNavigationService;

    private async Task<InlineKeyboardMarkup> GetFileSelKeyboardAsync(long userId, UserSession session)
    {
        (_, InlineKeyboardMarkup? keyboard) = await _navigationService.GetFilesViewAsync(userId, session.CurrentPath);
        return keyboard;
    }

    private async Task<InlineKeyboardMarkup> GetSectionSelKeyboardAsync(long userId, UserSession session)
    {
        (_, InlineKeyboardMarkup? keyboard) = await _navigationService.GetSectionsViewAsync(userId, session.CurrentPath);
        return keyboard;
    }

    private async Task<InlineKeyboardMarkup> GetProjectSelKeyboardAsync(long userId, UserSession session)
    {
        (_, InlineKeyboardMarkup? keyboard) = await _navigationService.GetProjectsViewAsync(userId, session.CurrentPath);
        return keyboard;
    }


    /// <summary>
    /// Возвращает клавиатуру выбора файлов с учетом текущего режима.
    /// </summary>
    public async Task<InlineKeyboardMarkup> GetSelectionKeyboardAsync(long userId, UserSession session)
    {
        InlineKeyboardMarkup keyboard = session.SelectionType switch
        {
            SelectionMode.Files => await GetFileSelKeyboardAsync(userId, session),
            SelectionMode.Sections => await GetSectionSelKeyboardAsync(userId, session),
            SelectionMode.Projects => await GetProjectSelKeyboardAsync(userId, session),
            _ => new InlineKeyboardMarkup(Array.Empty<InlineKeyboardButton[]>())
        };

        var newKeyboard = keyboard.InlineKeyboard.Select(row => row.Select(button =>
        {
            if (button.CallbackData != null && button.CallbackData.StartsWith(CallbackPrefixes.File))
                {
                    var token = button.CallbackData[CallbackPrefixes.File.Length..];

                if (_navigationService.TryResolvePath(userId, token, out var path) && path is not null && session.SelectedFiles.Contains(path))
                {
                    return InlineKeyboardButton.WithCallbackData($"✅ {Path.GetFileName(path)}", button.CallbackData);
                }
            }
            return button; // unchanged
        })
        .ToList()).ToList();

        return new InlineKeyboardMarkup(newKeyboard);
    }


    /// <summary>
    /// Возвращает клавиатуру выбора экспортных команд (PDF, DWG, NWC, IFC).
    /// </summary>
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

    /// <summary>
    /// Возвращает reply-клавиатуру с кнопками "Применить" и "Отмена" для экспорта.
    /// </summary>
    public Task<ReplyKeyboardMarkup> GetExportActionsReplyKeyboardAsync()
    {
        return Task.FromResult(BuildActionsReplyKeyboard(ButtonTexts.ExportApply));
    }

    /// <summary>
    /// Возвращает reply-клавиатуру с кнопками "Подтвердить" и "Отмена" для автоматизации.
    /// </summary>
    public Task<ReplyKeyboardMarkup> GetAutomationActionsReplyKeyboardAsync()
    {
        return Task.FromResult(BuildActionsReplyKeyboard(ButtonTexts.AutomationApply));
    }

    /// <summary>
    /// Возвращает reply-клавиатуру для действий с файлами.
    /// </summary>
    public Task<ReplyKeyboardMarkup> GetFileActionsReplyKeyboardAsync(UserSession session)
    {
        var applyButtonText = HasAutomationCommands(session)
            ? ButtonTexts.AutomationApply
            : ButtonTexts.ExportApply;

        return Task.FromResult(new ReplyKeyboardMarkup(
        [
            [new(applyButtonText), new(ButtonTexts.Cancel)]
        ])
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = false
        });
    }

    /// <summary>
    /// Возвращает клавиатуру списка сессий пользователя.
    /// </summary>
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

    /// <summary>
    /// Возвращает клавиатуру статуса сессии (подробности, удалить, назад).
    /// </summary>
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

    /// <summary>
    /// Возвращает клавиатуру команд внутри сессии (список файлов и команд).
    /// </summary>
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

    /// <summary>
    /// Возвращает клавиатуру выбора автоматизации (BIM Doctor, Clash Report, Auto Resolver).
    /// </summary>
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
        UserSession session,
        List<CommandOption> commandOptions)
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
            [
                    new(applyButtonText),
                    new(ButtonTexts.Cancel)
                ]
        ])
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = false
        };
    }

    private static bool HasAutomationCommands(UserSession session)
        => CommandCodes.AutomationCodes.Any(session.ContainsPendingCommand);
}
