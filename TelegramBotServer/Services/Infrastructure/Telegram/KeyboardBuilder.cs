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

        public async Task<InlineKeyboardMarkup> GetFileSelKeyboardAsync(long userId, UserSession session)
        {
            (_, InlineKeyboardMarkup? keyboard) = await _navigationService.GetFilesViewAsync(userId, session.CurrentPath);
            return keyboard;
        }

        public async Task<InlineKeyboardMarkup> GetSectionSelKeyboardAsync(long userId, UserSession session)
        {
            (_, InlineKeyboardMarkup? keyboard) = await _navigationService.GetSectionsViewAsync(userId, session.CurrentPath);
            return keyboard;
        }

        public async Task<InlineKeyboardMarkup> GetProjectSelKeyboardAsync(long userId, UserSession session)
        {
            (_, InlineKeyboardMarkup? keyboard) = await _navigationService.GetProjectsViewAsync(userId, session.CurrentPath);
            return keyboard;
        }


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
                if (button.CallbackData != null && button.CallbackData.StartsWith("FILE:"))
                {
                    var token = button.CallbackData[5..];

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


        public Task<InlineKeyboardMarkup> GetCommandsKeyboardAsync(UserSession session)
        {
            var commandOptions = new List<CommandOption>
            {
                new("Export to PDF", "PDF:", "PDF"),
                new("Export to DWG", "DWG:", "DWG"),
                new("Export to NWC", "NWC:", "NWC"),
                new("Export to IFC", "IFC:", "IFC")
            };

            return Task.FromResult(BuildSelectableCommandsKeyboard(session, commandOptions, ButtonTexts.ExportApply));
        }

        public Task<ReplyKeyboardMarkup> GetExportActionsReplyKeyboardAsync()
        {
            return Task.FromResult(BuildActionsReplyKeyboard(ButtonTexts.ExportApply));
        }

        public Task<ReplyKeyboardMarkup> GetAutomationActionsReplyKeyboardAsync()
        {
            return Task.FromResult(BuildActionsReplyKeyboard(ButtonTexts.AutomationApply));
        }

        public Task<InlineKeyboardMarkup> GetSessionsListKeyboardAsync(List<SessionsList> sessionsList)
        {
            var buttons = new List<List<InlineKeyboardButton>>();

            foreach (SessionsList session in sessionsList)
            {
                buttons.Add(
                [
                    InlineKeyboardButton.WithCallbackData(session.Date.ToString(), $"Sessiondetails:{session.SessionId}")
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
                    InlineKeyboardButton.WithCallbackData("More", $"Sessiondetails:{sessionId}"),
                    InlineKeyboardButton.WithCallbackData("Delete All", $"Deletesession:{sessionId}"),
                    InlineKeyboardButton.WithCallbackData("Back", $"Backtostatus:")
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
                    InlineKeyboardButton.WithCallbackData("Less", $"Sessiondetails:{sessionId}"),
                    InlineKeyboardButton.WithCallbackData("Delete All", $"Deletesession:{sessionId}"),
                    InlineKeyboardButton.WithCallbackData("Back", $"Backtostatus:")
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
                    InlineKeyboardButton.WithCallbackData("🗑 Delete", $"Deletecommand:{sessionCommand.CommandId}")
                ]);
            }

            return Task.FromResult(new InlineKeyboardMarkup(buttons));
        }

        public Task<InlineKeyboardMarkup> GetAutomationKeyboardAsync(UserSession session)
        {
            var commandOptions = new List<CommandOption>
            {
                new("BIM Doctor", "BIMDOC:", "BIMDOC"),
                new("Clash Report", "CLASHREP:", "CLASHREP"),
                new("Auto Resolver", "AUTORES:", "AUTORES")
            };

            return Task.FromResult(BuildSelectableCommandsKeyboard(session, commandOptions, ButtonTexts.AutomationApply));
        }

        private static InlineKeyboardMarkup BuildSelectableCommandsKeyboard(
            UserSession session,
            List<CommandOption> commandOptions,
            string applyButtonText)
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

            buttons.Add(
            [
                InlineKeyboardButton.WithCallbackData(applyButtonText, CallbackPrefixes.ApplyCommands),
                InlineKeyboardButton.WithCallbackData(ButtonTexts.Cancel, CallbackPrefixes.CancelCommandSelection)
            ]);

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
    }
