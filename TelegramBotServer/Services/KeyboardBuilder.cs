using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services
{
    public class KeyboardBuilder(IFileSystemBrowser fileNavigationService) : IKeyboardBuilder
    {
        //GetDirectoryKeyboard

        //private readonly ISessionManager _sessionManger;
        private readonly IFileSystemBrowser _navigationService = fileNavigationService;

        public async Task<InlineKeyboardMarkup> GetFileSelKeyboardAsync(long userId, UserSession session)
        {
            var (message, keyboard) = await _navigationService.GetFilesViewAsync(userId, session.CurrentPath);

            return keyboard;
        }

        public async Task<InlineKeyboardMarkup> GetSectionSelKeyboardAsync(long userId, UserSession session)
        {
            var (message, keyboard) = await _navigationService.GetSectionsViewAsync(userId, session.CurrentPath);

            return keyboard;
        }

        public async Task<InlineKeyboardMarkup> GetProjectSelKeyboardAsync(long userId, UserSession session)
        {
            var (message, keyboard) = await _navigationService.GetProjectsViewAsync(userId, session.CurrentPath);

            return keyboard;
        }


        public async Task<InlineKeyboardMarkup> GetSelectionKeyboardAsync(long userId, UserSession session)
        {
            var keyboard = session.SelectionType switch
            {
                1 => await GetFileSelKeyboardAsync(userId, session),
                2 => await GetSectionSelKeyboardAsync(userId, session),
                3 => await GetProjectSelKeyboardAsync(userId, session),
                _ => new InlineKeyboardMarkup(Array.Empty<InlineKeyboardButton[]>())
            };


            var newKeyboard = keyboard.InlineKeyboard.Select(row => row.Select(button =>
            {
                if (button.CallbackData != null && button.CallbackData.StartsWith("FILE:"))
                {
                    var token = button.CallbackData.Substring(5);
                    if (_navigationService.TryResolvePath(userId, token, out var path) &&
                        path is not null &&
                        session.SelectedFiles.Contains(path))
                    {
                        return InlineKeyboardButton.WithCallbackData($"✅ {Path.GetFileName(path)}", button.CallbackData);
                    }
                }
                return button; // unchanged
            })
            .ToList()
                ).ToList();
            return new InlineKeyboardMarkup(newKeyboard);
        }



        public async Task<InlineKeyboardMarkup> GetCommandsKeyboardAsync(long userId, UserSession session)
        {
            var buttons = new List<List<InlineKeyboardButton>>();

            buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("Export to PDF", "PDF:")
                        });

            buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("Export to DWG", "DWG:")
                        });

            buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("Export to NWC", "NWC:")
                        });
            buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("Export to IFC", "IFC:")
                        });
            buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("✅ Применить", "APPLYCOMMANDS:")
                        });
            buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("❌ Отмена", "CANCELCOMMANDSSEL:")
                        });

            var keyboard = new InlineKeyboardMarkup(buttons);

            var newKeyboard = keyboard.InlineKeyboard.Select(row => row.Select(button =>
            {
                if (button.CallbackData != null && !button.CallbackData.StartsWith("APPLYCOMMANDS:") && !button.CallbackData.StartsWith("CANCELCOMMANDSSEL:"))
                {
                    if (session.PendingCommandName.Contains(button.Text))
                    {
                        return InlineKeyboardButton.WithCallbackData($"✅ {button.Text}", button.CallbackData);
                    }
                }
                return button; // unchanged
            })
            .ToList()
            ).ToList();


            return new InlineKeyboardMarkup(newKeyboard);
        }
        public async Task<InlineKeyboardMarkup> GetSessionsListKeyboardAsync(List<SessionsList> sessionsList)
        {

            var buttons = new List<List<InlineKeyboardButton>>();

            foreach (var session in sessionsList)
            {
                buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData(session.Date.ToString(), $"Sessiondetails:{session.SessionId}")
                        });
            }
            return await Task.FromResult(new InlineKeyboardMarkup(buttons));

        }
        public async Task<InlineKeyboardMarkup> GetSessionStatusKeyboardAsync(SessionStatus sessionStatus, int sessionId)
        {
            _ = sessionStatus;

            var buttons = new List<List<InlineKeyboardButton>>();


            buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("More", $"Sessiondetails:{sessionId}"),
                            InlineKeyboardButton.WithCallbackData("Delete All", $"Deletesession:{sessionId}"),
                            InlineKeyboardButton.WithCallbackData("Back", $"Backtostatus:")
                        });
            return await Task.FromResult(new InlineKeyboardMarkup(buttons));

        }
        public async Task<InlineKeyboardMarkup> GetSessionCommandsKeyboardAsync(List<SessionCommands> sessionCommands, int sessionId)
        {

            var buttons = new List<List<InlineKeyboardButton>>();


            buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("Less", $"Sessiondetails:{sessionId}"),
                            InlineKeyboardButton.WithCallbackData("Delete All", $"Deletesession:{sessionId}"),
                            InlineKeyboardButton.WithCallbackData("Back", $"Backtostatus:")
                        });

            foreach (var sessionCommand in sessionCommands)
            {
                buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData($"{sessionCommand.ExecOrder} | {sessionCommand.Command} | {Path.GetFileName(sessionCommand.FileName)} | {sessionCommand.Status}", $"{sessionCommand.CommandId}"),
                        });
                buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData($"{sessionCommand.Date}", $"{sessionCommand.CommandId}"),
                            InlineKeyboardButton.WithCallbackData("🗑 Delete", $"Deletecommand:{sessionCommand.CommandId}")
                        });
            }

            return await Task.FromResult(new InlineKeyboardMarkup(buttons));

        }

        public async Task<InlineKeyboardMarkup> GetAutomationKeyboardAsync(long userId, UserSession session)
        {
            var buttons = new List<List<InlineKeyboardButton>>();

            buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("BIM Doctor", "BIMDOC:")
                        });

            buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("Clash Report", "CLASHREP:")
                        });

            buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("Auto Resolver", "AUTORES:")
                        });

            buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("✅ Подтвердить", "APPLYCOMMANDS:")
                        });
            buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("❌ Отмена", "CANCELCOMMANDSSEL:")
                        });



            var keyboard = new InlineKeyboardMarkup(buttons);

            var newKeyboard = keyboard.InlineKeyboard.Select(row => row.Select(button =>
            {
                if (button.CallbackData != null && !button.CallbackData.StartsWith("APPLYCOMMANDS:") && !button.CallbackData.StartsWith("CANCELCOMMANDSSEL:"))
                {
                    if (session.PendingCommand.Contains(button.CallbackData.Replace(":", "")))
                    {
                        return InlineKeyboardButton.WithCallbackData($"✅ {button.Text}", button.CallbackData);
                    }
                }
                return button; // unchanged
            })
            .ToList()
            ).ToList();

            return new InlineKeyboardMarkup(newKeyboard);
        }

    }
}
