using System.Text.RegularExpressions;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotServer.DTOs;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services
{
    public class CommandAppService(IDataService dataService, ITelegramOutputService outputService,
        INavigationService fileNavigationService, ISessionManager sessionManager, IKeyboardBuilder keyboardBuilder) : ICommandAppService
    {
        private readonly IDataService _dataService = dataService;
        private readonly ITelegramOutputService _outputService = outputService;
        private readonly INavigationService _fileNavigationService = fileNavigationService;
        private readonly ISessionManager _sessionManager = sessionManager;
        private readonly IKeyboardBuilder _keyboardBuilder = keyboardBuilder;

        string rootPath = "I:\\";

        public async Task HandleUserCommandAsync(MessageDto message)
        {

            if (message.Username == null || message.Text == null)
            {
                await _outputService.SendMessageAsync(message.UserId, "Username or text is empty.");
                return;
            }

            //public long UserId = message.UserId;
            long userId = message.UserId;
            string username = message.Username;
            long chatId = message.ChatId;
            string text = message.Text;
            DateTime date = message.Date;
            int messageId = message.MessageId;


            Console.WriteLine($"[Controller] Received command '{text}' from {username} ({userId})");
            var session = _sessionManager.GetOrCreateSession(userId);

            //if status
            //provide list of users commands
            //provide ability to unqueue files by one or by group

            //ask infrastructure for list of commands//later
            //compare text with list of commands//later
            //if matches////later

            switch (text.ToLower())
            {
                case "/export":
                    {
                        //send interface to select files.
                        session.SelectedFiles.Clear();
                        session.PendingCommand.Clear();
                        session.PendingCommandName.Clear();
                        session.CurrentPath = rootPath;
                        session.PagesCache.Clear();
                        //await _outputService.DeleteMessageAsync(chatId, messageId);
                        //var (message2, keyboard) = await _fileNavigationService.GetDirectoryViewAsync(userId, session.CurrentPath);

                        session.Counter = 0;
                        session.Items.Clear();







                        InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetCommandsKeyboardAsync(userId, session);

                        await _outputService.SendMessageWithKeyboardAsync(userId, $"Выберите команду:", keyboard);



                        //execute add command to queue(prioritization feature)--in callbackhandler*
                        //notify user--in callbackhandler*
                        break;
                    }
                case "/status":
                    {
                        //clear
                        //send interface to select files.
                        session.SelectedFiles.Clear();
                        session.PendingCommand.Clear();
                        session.PendingCommandName.Clear();
                        session.CurrentPath = rootPath;
                        session.PagesCache.Clear();
                        //await _outputService.DeleteMessageAsync(chatId, messageId);
                        //var (message2, keyboard) = await _fileNavigationService.GetDirectoryViewAsync(userId, session.CurrentPath);

                        session.Counter = 0;
                        session.Items.Clear();


                        session.statusLevel = true;
                        List<SessionsList> sessionsStatus = await _dataService.GetSessionsListAsync(userId);
                        InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSessionsListKeyboardAsync(sessionsStatus);
                        await _outputService.SendMessageWithKeyboardAsync(userId, $"Сессии:", keyboard);

                        break;
                    }
                case "/automation":
                    {
                        //send interface to select files.
                        session.SelectedFiles.Clear();
                        session.PendingCommand.Clear();
                        session.PendingCommandName.Clear();
                        session.CurrentPath = rootPath;
                        session.PagesCache.Clear();
                        //await _outputService.DeleteMessageAsync(chatId, messageId);
                        //var (message2, keyboard) = await _fileNavigationService.GetDirectoryViewAsync(userId, session.CurrentPath);

                        session.Counter = 0;
                        session.Items.Clear();


                        InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetAutomationKeyboardAsync(userId, session);

                        await _outputService.SendMessageWithKeyboardAsync(userId, $"Выберите команду:", keyboard);



                        //execute add command to queue(prioritization feature)--in callbackhandler*
                        //notify user--in callbackhandler*
                        break;
                    }
                case "/help":
                    //send interface to select files.
                    session.SelectedFiles.Clear();
                    session.PendingCommand.Clear();
                    session.PendingCommandName.Clear();
                    session.CurrentPath = rootPath;
                    session.PagesCache.Clear();
                    //await _outputService.DeleteMessageAsync(chatId, messageId);
                    //var (message2, keyboard) = await _fileNavigationService.GetDirectoryViewAsync(userId, session.CurrentPath);

                    session.Counter = 0;
                    session.Items.Clear();




                    await _outputService.SendMessageAsync(userId,
                        "/export - используется для экспорта в форматы PDF, DWG, NWC, IFC.\n" +
                        "При отправке данной команды будет пользователю будут предоставлены форматы на выбор(можно выбрать несколько сразу)\n" +
                        "Затем пользователю предоставляется меню навигации по файлохранилищу для выбора файлов для экспорта(можно выбрать несколько),\n" +
                        "Кроме того пользователь может изменить режим выбора файлов: отдельные файлы, разделы, целые проекты.\n" +
                        "При этом все файлы внутри раздела, проекта будут автоматически выбраны.\n" +
                        "По нажатию на кнопку 'Выполнить' файлы отправятся на сервер для обработки.\n" +
                        "/automation - используется для автоматизации задач. Прим. BIM Doctor, Clash Report, Auto Resolver\n" +
                        "При отправке данной команды будет пользователю будут предоставлены команды автоматизации на выбор(можно выбрать несколько сразу)\n" +
                        "Затем пользователю предоставляется меню навигации по файлохранилищу для выбора файлов(можно выбрать несколько),\n" +
                        "Кроме того пользователь может изменить режим выбора файлов: отдельные файлы, разделы, целые проекты.\n" +
                        "При этом все файлы внутри раздела, проекта будут автоматически выбраны.\n" +
                        "По нажатию на кнопку 'Выполнить' файлы отправятся на сервер для обработки.\n" +
                        "/status - используется для проверки состояния выполнения команды отправленной пользователем.\n" +
                        "При отправке данной команды пользователю будет предоставлени список сессии с временем отправки на обработку.\n" +
                        "Пользователь может нажать на сессию для мониторинга процесса выполнения команды.\n" +
                        "Кроме того в предоставленном меню пользователь может полностью удалить сессию\n" +
                        "Также в предоставленном меню пользователь может раскрыть список файлов в данной сессии для мониторинга и удаления отдельных файлов в сессии."
                        );

                    break;
                default:
                    break;
            }



            //send interface to select files.
            //execute add command to queue(prioritization feature)
            //notify user

            //user notifications of command processing completion
            //delete messages

        }





        public async Task HandleCallbackAsync(CallbackQueryDto callback)
        {
            if (callback.Username == null || callback.MessageText == null || callback.CallbackData == null || callback.CallbackQueryId == null)
            {
                await _outputService.SendMessageAsync(callback.UserId, "Callback is empty.");
                return;
            }

            long userId = callback.UserId;
            string username = callback.Username;
            long chatId = callback.ChatId;
            string messageText = callback.MessageText;
            int messageId = callback.MessageId;
            string callbackData = callback.CallbackData;
            string callbackQueryId = callback.CallbackQueryId;
            List<List<ButtonDto>> buttonDtos = callback.Buttons;


            var session = _sessionManager.GetOrCreateSession(userId);




            if (session.SelectionType == 1)
            {
                if (callbackData.StartsWith("NAV"))
                {
                    session.PagesCache.Add(session.Counter);
                    session.Counter = 0;


                    if (callbackData.StartsWith("NAV2"))
                    {
                        if (session.PagesCache.Count > 0)
                            session.PagesCache.RemoveAt(session.PagesCache.Count - 1);
                        session.Counter = session.PagesCache.Last();
                    }



                    var token = callbackData.Substring(5);
                    if (!_fileNavigationService.TryResolvePath(userId, token, out var newPath))
                    {
                        await _outputService.SendErrorAsync(userId, "Path not found.");
                        return;
                    }

                    if (newPath == null)
                    {
                        await _outputService.SendErrorAsync(userId, "Path not found.");
                        return;
                    }

                    session.CurrentPath = newPath!;

                    await _outputService.AnswerCallbackAsync(callbackQueryId, session.CurrentPath);

                    InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);

                    await _outputService.EditMessageReplyMarkupAsync(
                        userId,
                        messageId,
                        keyboard
                    );
                }
                else if (callbackData.StartsWith("FILE:"))
                {



                    var token = callbackData.Substring(5);
                    if (!_fileNavigationService.TryResolvePath(userId, token, out var filePath))
                    {
                        await _outputService.SendErrorAsync(userId, "File not found.");
                        return;
                    }



                    if (filePath != null && session.SelectedFiles.Contains(filePath))
                    {
                        session.SelectedFiles.Remove(filePath);
                    }
                    else if (filePath != null)
                    {
                        session.SelectedFiles.Add(filePath);
                    }


                    InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);

                    await _outputService.EditMessageReplyMarkupAsync(
                        userId,
                        messageId,
                        keyboard
                    );
                }

                else if (callbackData.StartsWith("APPLYFILES:"))
                {
                    if (session.SelectedFiles.Count > 0)
                    {
                        //await _dataService.AddCommandAsync(userId, session.SelectedFiles, session.PendingCommand);

                        await _dataService.CreateSessionWithCommandsAsync(session.PendingCommand, session.SelectedFiles, userId, username, session.SelectionType, session.SelectedFiles.Count());


                        //await _outputService.DeleteMessageAsync(chatId, messageId);

                        string reply = "Команда:\n";
                        foreach (var file in session.PendingCommandName)
                        {
                            reply = string.Concat(reply, "? " + Path.GetFileName(file) + "\n");
                        }



                        reply = string.Concat(reply, "Добавлены файлы:\n");
                        foreach (var file in session.SelectedFiles)
                        {
                            reply = string.Concat(reply, "? " + Path.GetFileName(file) + "\n");
                        }

                        reply = string.Concat(reply, "\n/status для проверки статуса команды");


                        await _outputService.EditMessageReplyTextAsync(userId, messageId, reply);



                        //foreach (var file in session.SelectedFiles)
                        //{
                        //    await _outputService.SendMessageAsync(userId, $"? Added to queue: {Path.GetFileName(file)}");
                        //}



                        session.SelectedFiles.Clear();
                        session.Items.Clear();
                        session.PagesCache.Clear();
                        session.SelectionType = 1;




                        //clear session?
                    }
                }

                else if (callbackData.StartsWith("CANCELSEL:"))
                {
                    session.SelectedFiles.Clear();


                    InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);

                    await _outputService.EditMessageReplyMarkupAsync(
                        userId,
                        messageId,
                        keyboard
                    );
                }
                else if (callbackData.StartsWith("CANCELFILESEL:"))
                {
                    session.SelectedFiles.Clear();
                    session.CurrentPath = rootPath;
                    session.Counter = 0;
                    session.Items.Clear();
                    session.PagesCache.Clear();
                    session.SelectionType = 1;
                    //await _outputService.DeleteMessageAsync(chatId, messageId);
                    //get editedcommandskeyboard

                    if (session.PendingCommand.Contains("PDF") || session.PendingCommand.Contains("DWG") || session.PendingCommand.Contains("NWC") || session.PendingCommand.Contains("IFC"))
                    {
                        InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetCommandsKeyboardAsync(userId, session);
                        await _outputService.EditMessageReplyMarkupAsync(
                        userId,
                        messageId,
                        keyboard
                    );
                    }

                    if (session.PendingCommand.Contains("BIMDOC") || session.PendingCommand.Contains("CLASHREP") || session.PendingCommand.Contains("AUTORES"))
                    {
                        InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetAutomationKeyboardAsync(userId, session);
                        await _outputService.EditMessageReplyMarkupAsync(
                        userId,
                        messageId,
                        keyboard
                    );

                    }
                }
            }
            else if (session.SelectionType == 2)
            {
                if (callbackData.StartsWith("NAV"))
                {
                    session.PagesCache.Add(session.Counter);
                    session.Counter = 0;

                    session.Level = true;
                    if (callbackData.StartsWith("NAV2"))
                    {
                        session.Level = false;
                        if (session.PagesCache.Count > 0)
                            session.PagesCache.RemoveAt(session.PagesCache.Count - 1);
                        session.Counter = session.PagesCache.Last();
                    }

                    var token = callbackData.Substring(5);
                    if (!_fileNavigationService.TryResolvePath(userId, token, out var newPath))
                    {
                        await _outputService.SendErrorAsync(userId, "Path not found.");
                        return;
                    }




                    if (newPath == null)
                    {
                        await _outputService.SendErrorAsync(userId, "Path not found.");
                        return;
                    }

                    if (callbackData.StartsWith("NAV2"))
                    {
                        session.CurrentPath = rootPath;
                    }
                    else
                    {
                        session.CurrentPath = newPath! + "\\01_PROJECT";
                    }




                    await _outputService.AnswerCallbackAsync(callbackQueryId, session.CurrentPath);

                    InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);

                    await _outputService.EditMessageReplyMarkupAsync(
                        userId,
                        messageId,
                        keyboard
                    );
                }
                else if (callbackData.StartsWith("FILE:"))
                {



                    var token = callbackData.Substring(5);
                    if (!_fileNavigationService.TryResolvePath(userId, token, out var filePath))
                    {
                        await _outputService.SendErrorAsync(userId, "File not found.");
                        return;
                    }



                    if (filePath != null && session.SelectedFiles.Contains(filePath))
                    {
                        session.SelectedFiles.Remove(filePath);
                    }
                    else if (filePath != null)
                    {

                        session.SelectedFiles.Add(filePath);
                    }


                    InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);

                    await _outputService.EditMessageReplyMarkupAsync(
                        userId,
                        messageId,
                        keyboard
                    );
                }

                else if (callbackData.StartsWith("APPLYFILES:"))
                {
                    if (session.SelectedFiles.Count > 0)
                    {
                        session.SelectedFiles = MapSectionsToFiles(session.SelectedFiles);

                        await _dataService.CreateSessionWithCommandsAsync(session.PendingCommand, session.SelectedFiles, userId, username, session.SelectionType, session.SelectedFiles.Count());


                        string reply = "Команда:\n";
                        foreach (var file in session.PendingCommandName)
                        {
                            reply = string.Concat(reply, "? " + Path.GetFileName(file) + "\n");
                        }



                        reply = string.Concat(reply, "Добавлены файлы:\n");
                        foreach (var file in session.SelectedFiles)
                        {
                            reply = string.Concat(reply, "? " + Path.GetFileName(file) + "\n");
                        }

                        reply = string.Concat(reply, "\n/status для проверки статуса команды");



                        await _outputService.EditMessageReplyTextAsync(userId, messageId, reply);


                        session.SelectedFiles.Clear();
                        session.PagesCache.Clear();
                        session.Level = false;
                        session.SelectionType = 1;
                    }
                }

                else if (callbackData.StartsWith("CANCELSEL:"))
                {
                    session.SelectedFiles.Clear();


                    InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);

                    await _outputService.EditMessageReplyMarkupAsync(
                        userId,
                        messageId,
                        keyboard
                    );
                }
                else if (callbackData.StartsWith("CANCELFILESEL:"))
                {
                    session.SelectedFiles.Clear();
                    session.CurrentPath = rootPath;
                    session.Level = false;
                    session.PagesCache.Clear();
                    session.SelectionType = 1;
                    //await _outputService.DeleteMessageAsync(chatId, messageId);
                    //get editedcommandskeyboard


                    if (session.PendingCommand.Contains("PDF") || session.PendingCommand.Contains("DWG") || session.PendingCommand.Contains("NWC") || session.PendingCommand.Contains("IFC"))
                    {
                        InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetCommandsKeyboardAsync(userId, session);
                        await _outputService.EditMessageReplyMarkupAsync(
                        userId,
                        messageId,
                        keyboard
                    );
                    }

                    if (session.PendingCommand.Contains("BIMDOC") || session.PendingCommand.Contains("CLASHREP") || session.PendingCommand.Contains("AUTORES"))
                    {
                        InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetAutomationKeyboardAsync(userId, session);
                        await _outputService.EditMessageReplyMarkupAsync(
                        userId,
                        messageId,
                        keyboard
                    );

                    }
                }
            }
            else if (session.SelectionType == 3)
            {

                if (callbackData.StartsWith("FILE:"))
                {



                    var token = callbackData.Substring(5);
                    if (!_fileNavigationService.TryResolvePath(userId, token, out var filePath))
                    {
                        await _outputService.SendErrorAsync(userId, "File not found.");
                        return;
                    }



                    if (filePath != null && session.SelectedFiles.Contains(filePath))
                    {
                        session.SelectedFiles.Remove(filePath);
                    }
                    else if (filePath != null)
                    {
                        session.SelectedFiles.Add(filePath);
                    }


                    InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);

                    await _outputService.EditMessageReplyMarkupAsync(
                        userId,
                        messageId,
                        keyboard
                    );
                }

                else if (callbackData.StartsWith("APPLYFILES:"))
                {
                    if (session.SelectedFiles.Count > 0)
                    {

                        session.SelectedFiles = MapProjectsToFiles(session.SelectedFiles);



                        await _dataService.CreateSessionWithCommandsAsync(session.PendingCommand, session.SelectedFiles, userId, username, session.SelectionType, session.SelectedFiles.Count());




                        string reply = "Команда:\n";
                        foreach (var file in session.PendingCommandName)
                        {
                            reply = string.Concat(reply, "? " + Path.GetFileName(file) + "\n");
                        }



                        reply = string.Concat(reply, "Добавлены файлы:\n");
                        foreach (var file in session.SelectedFiles)
                        {
                            reply = string.Concat(reply, "? " + Path.GetFileName(file) + "\n");
                        }

                        reply = string.Concat(reply, "\n/status для проверки статуса команды");


                        await _outputService.EditMessageReplyTextAsync(userId, messageId, reply);


                        session.SelectedFiles.Clear();
                        session.PagesCache.Clear();
                        session.SelectionType = 1;
                    }
                }

                else if (callbackData.StartsWith("CANCELSEL:"))
                {
                    session.SelectedFiles.Clear();


                    InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);

                    await _outputService.EditMessageReplyMarkupAsync(
                        userId,
                        messageId,
                        keyboard
                    );
                }
                else if (callbackData.StartsWith("CANCELFILESEL:"))
                {
                    session.SelectedFiles.Clear();
                    session.CurrentPath = rootPath;
                    session.PagesCache.Clear();
                    session.SelectionType = 1;
                    //await _outputService.DeleteMessageAsync(chatId, messageId);
                    //get editedcommandskeyboard


                    if (session.PendingCommand.Contains("PDF") || session.PendingCommand.Contains("DWG") || session.PendingCommand.Contains("NWC") || session.PendingCommand.Contains("IFC"))
                    {
                        InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetCommandsKeyboardAsync(userId, session);
                        await _outputService.EditMessageReplyMarkupAsync(
                        userId,
                        messageId,
                        keyboard
                    );
                    }

                    if (session.PendingCommand.Contains("BIMDOC") || session.PendingCommand.Contains("CLASHREP") || session.PendingCommand.Contains("AUTORES"))
                    {
                        InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetAutomationKeyboardAsync(userId, session);
                        await _outputService.EditMessageReplyMarkupAsync(
                        userId,
                        messageId,
                        keyboard );
                    }

                }
            }


            if (callbackData.StartsWith("SELMODE:"))
            {
                int nextMode = session.SelectionType switch
                {
                    1 => 2,
                    2 => 3,
                    3 => 1,
                    _ => 1
                };
                session.SelectionType = nextMode;
                session.CurrentPath = rootPath;
                session.Level = false;
                session.SelectedFiles.Clear();
                session.Counter = 0;
                session.PagesCache.Clear();


                InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);

                await _outputService.EditMessageReplyMarkupAsync(
                    userId,
                    messageId,
                    keyboard
                );
            }



            else if (callbackData.StartsWith("NEXT:"))
            {
                session.Counter += 20;
                InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);

                await _outputService.EditMessageReplyMarkupAsync(
                    userId,
                    messageId,
                    keyboard
                );
            }

            else if (callbackData.StartsWith("PREV:"))
            {
                session.Counter -= 20;

                InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);

                await _outputService.EditMessageReplyMarkupAsync(
                    userId,
                    messageId,
                    keyboard
                );
            }

            else if (callbackData.StartsWith("PDF:"))
            {
                if (session.PendingCommand.Contains("PDF"))
                {
                    session.PendingCommand.Remove("PDF");
                    session.PendingCommandName.Remove("Export to PDF");
                }
                else
                {
                    session.PendingCommand.Add("PDF");
                    session.PendingCommandName.Add("Export to PDF");
                }
                //get editedcommandskeyboard
                InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetCommandsKeyboardAsync(userId, session);
                await _outputService.EditMessageReplyMarkupAsync(
                    userId,
                    messageId,
                    keyboard
                );
            }

            else if (callbackData.StartsWith("DWG:"))
            {
                if (session.PendingCommand.Contains("DWG"))
                {
                    session.PendingCommand.Remove("DWG");
                    session.PendingCommandName.Remove("Export to DWG");
                }
                else
                {
                    session.PendingCommand.Add("DWG");
                    session.PendingCommandName.Add("Export to DWG");
                }
                //get editedcommandskeyboard
                InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetCommandsKeyboardAsync(userId, session);
                await _outputService.EditMessageReplyMarkupAsync(
                    userId,
                    messageId,
                    keyboard
                );
            }
            else if (callbackData.StartsWith("NWC:"))
            {
                if (session.PendingCommand.Contains("NWC"))
                {
                    session.PendingCommand.Remove("NWC");
                    session.PendingCommandName.Remove("Export to NWC");
                }
                else
                {
                    session.PendingCommand.Add("NWC");
                    session.PendingCommandName.Add("Export to NWC");
                }
                //get editedcommandskeyboard
                InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetCommandsKeyboardAsync(userId, session);
                await _outputService.EditMessageReplyMarkupAsync(
                    userId,
                    messageId,
                    keyboard
                );
            }
            else if (callbackData.StartsWith("IFC:"))
            {
                if (session.PendingCommand.Contains("IFC"))
                {
                    session.PendingCommand.Remove("IFC");
                    session.PendingCommandName.Remove("Export to IFC");
                }
                else
                {
                    session.PendingCommand.Add("IFC");
                    session.PendingCommandName.Add("Export to IFC");
                }
                //get editedcommandskeyboard
                InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetCommandsKeyboardAsync(userId, session);
                await _outputService.EditMessageReplyMarkupAsync(
                    userId,
                    messageId,
                    keyboard
                );
            }
            else if (callbackData.StartsWith("APPLYCOMMANDS:"))
            {
                if (session.PendingCommand.Count > 0)
                {
                    session.CurrentPath = rootPath;
                    InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);
                    await _outputService.EditMessageReplyMarkupAsync(userId, messageId, keyboard);
                }

            }
            else if (callbackData.StartsWith("CANCELCOMMANDSSEL:"))
            {
                session.PendingCommand.Clear();
                session.PendingCommandName.Clear();
                await _outputService.DeleteMessageAsync(chatId, messageId);
            }

            else if (callbackData.StartsWith("CANCELCOMMANDSSEL:"))
            {
                session.PendingCommand.Clear();
                session.PendingCommandName.Clear();
                await _outputService.DeleteMessageAsync(chatId, messageId);
            }

            else if (callbackData.StartsWith("Sessiondetails:") && session.statusLevel == true)
            {
                session.statusLevel = false;


                var token = callbackData.Substring(15);

                int.TryParse(token, out int tkn);

                session.sessionId = tkn;

                SessionStatus sessionStatus = await _dataService.GetSessionsStatusAsync(tkn);

                int percentage = (100 * sessionStatus.DoneFiles) / sessionStatus.TotalFiles;

                string reply = $"Статус: {sessionStatus.Status}\nФайлов: {sessionStatus.TotalFiles}\nЗавершено: {sessionStatus.DoneFiles}\n{percentage}%";

                await _outputService.EditMessageReplyTextAsync(userId, messageId, reply);

                InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSessionStatusKeyboardAsync(sessionStatus, tkn);

                await _outputService.EditMessageReplyMarkupAsync(
                    userId,
                    messageId,
                    keyboard
                );

                await _outputService.EditMessageReplyMarkupAsync(userId, messageId, keyboard);
            }
            else if (callbackData.StartsWith("Sessiondetails:") && session.statusLevel == false)
            {
                session.statusLevel = true;

                var token = callbackData.Substring(14);

                int.TryParse(token, out int tkn);

                List<SessionCommands> sessionCommands = await _dataService.GetSessionsCommandsAsync(tkn);

                InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSessionCommandsKeyboardAsync(sessionCommands, tkn);
                await _outputService.EditMessageReplyMarkupAsync(
                    userId,
                    messageId,
                    keyboard
                );
            }
            else if (callbackData.StartsWith("Backtostatus:"))
            {
                session.statusLevel = true;
                List<SessionsList> sessionsStatus = await _dataService.GetSessionsListAsync(userId);
                InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSessionsListKeyboardAsync(sessionsStatus);
                await _outputService.EditMessageReplyTextAsync(userId, messageId, "Сессии:");
                await _outputService.EditMessageReplyMarkupAsync(
                    userId,
                    messageId,
                    keyboard
                );
            }
            else if (callbackData.StartsWith("Deletesession:"))
            {
                var token = callbackData.Substring(14);
                int.TryParse(token, out int tkn);

                //call sqldataservice to delete session
                //deleted sessions won't be shown

                if (await _dataService.DeleteSessionAsync(tkn))
                {
                    session.statusLevel = true;
                    List<SessionsList> sessionsStatus = await _dataService.GetSessionsListAsync(userId);
                    InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSessionsListKeyboardAsync(sessionsStatus);
                    await _outputService.EditMessageReplyTextAsync(userId, messageId, "Сессии:");
                    await _outputService.EditMessageReplyMarkupAsync(
                        userId,
                        messageId,
                        keyboard
                    );
                }

            }
            else if (callbackData.StartsWith("Deletecommand:"))
            {
                var token = callbackData.Substring(15);

                int.TryParse(token, out int tkn);

                if (await _dataService.DeleteCommandAsync(tkn))
                {
                    foreach (var row in buttonDtos)
                    {
                        row.RemoveAll(btn => btn.CallbackData!.Contains($"{tkn}"));
                    }

                    var newKeyboard = ConvertDtoToKeyboard(buttonDtos);

                    //check if all files are deleted, if yes then mark session as deleted and return to sessions page.
                    if (!await _dataService.CheckCommandsStatusAsync(session.sessionId))
                    {
                        if (await _dataService.DeleteSessionAsync(session.sessionId))
                        {
                            session.statusLevel = true;
                            List<SessionsList> sessionsStatus = await _dataService.GetSessionsListAsync(userId);
                            InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSessionsListKeyboardAsync(sessionsStatus);
                            await _outputService.EditMessageReplyTextAsync(userId, messageId, "Сессии:");
                            await _outputService.EditMessageReplyMarkupAsync(
                                userId,
                                messageId,
                                keyboard
                            );
                        }
                    }
                    else
                    {
                        SessionStatus sessionStatus = await _dataService.GetSessionsStatusAsync(session.sessionId);

                        int percentage = (100 * sessionStatus.DoneFiles) / sessionStatus.TotalFiles;

                        string reply = $"Статус: {sessionStatus.Status}\nФайлов: {sessionStatus.TotalFiles}\nЗавершено: {sessionStatus.DoneFiles}\n{percentage}%";

                        await _outputService.EditMessageReplyTextAsync(userId, messageId, reply);

                        await _outputService.EditMessageReplyMarkupAsync(
                            userId,
                            messageId,
                            newKeyboard
                            );
                    }
                }
            }
            else if (callbackData.StartsWith("BIMDOC:"))
            {
                if (session.PendingCommand.Contains("BIMDOC"))
                {
                    session.PendingCommand.Remove("BIMDOC");
                    session.PendingCommandName.Remove("BIMDOC");
                }
                else
                {
                    session.PendingCommand.Add("BIMDOC");
                    session.PendingCommandName.Add("BIMDOC");
                }
                //get editedcommandskeyboard
                InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetAutomationKeyboardAsync(userId, session);
                await _outputService.EditMessageReplyMarkupAsync(
                    userId,
                    messageId,
                    keyboard
                );
            }
            else if (callbackData.StartsWith("CLASHREP:"))
            {
                if (session.PendingCommand.Contains("CLASHREP"))
                {
                    session.PendingCommand.Remove("CLASHREP");
                    session.PendingCommandName.Remove("CLASHREP");
                }
                else
                {
                    session.PendingCommand.Add("CLASHREP");
                    session.PendingCommandName.Add("CLASHREP");
                }
                //get editedcommandskeyboard
                InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetAutomationKeyboardAsync(userId, session);
                await _outputService.EditMessageReplyMarkupAsync(
                    userId,
                    messageId,
                    keyboard
                );
            }
            else if (callbackData.StartsWith("AUTORES:"))
            {
                if (session.PendingCommand.Contains("AUTORES"))
                {
                    session.PendingCommand.Remove("AUTORES");
                    session.PendingCommandName.Remove("AUTORES");
                }
                else
                {
                    session.PendingCommand.Add("AUTORES");
                    session.PendingCommandName.Add("AUTORES");
                }
                //get editedcommandskeyboard
                InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetAutomationKeyboardAsync(userId, session);
                await _outputService.EditMessageReplyMarkupAsync(
                    userId,
                    messageId,
                    keyboard
                );
            }
        }


        public InlineKeyboardMarkup ConvertDtoToKeyboard(List<List<ButtonDto>> dto)
        {
            if (dto == null)
                return new InlineKeyboardMarkup(Array.Empty<InlineKeyboardButton[]>());

            var inlineKeyboard = dto
                .Select(row => row
                    .Select(btn => InlineKeyboardButton.WithCallbackData(
                        btn.Text ?? "",
                        btn.CallbackData ?? ""))
                    .ToArray())
                .ToArray();

            return new InlineKeyboardMarkup(inlineKeyboard);
        }



        public List<string> MapSectionsToFiles(List<string> dirs)
        {

            List<string> allFiles = new List<string>();
            for (int i = 0; i < dirs.Count; i++)
            {
                dirs[i] = Path.Combine(dirs[i], "01_RVT");
                var files = Directory.GetFiles(dirs[i]);
                foreach (var file in files)
                {
                    if (file.Contains(".rvt"))
                        allFiles.Add(file);
                }
            }
            return allFiles;
        }



        public List<string> MapProjectsToFiles(List<string> dirs)
        {
            List<string> allFiles = new List<string>();
            //List<string> sections = new List<string>();
            for (int i = 0; i < dirs.Count; i++)
            {
                dirs[i] = Path.Combine(dirs[i], "01_PROJECT");
                var tempSections = Directory.GetDirectories(dirs[i]);
                Regex roman3 = new Regex(@"^III_", RegexOptions.IgnoreCase);

                var sections = tempSections.Where(d =>
                {
                    string name = Path.GetFileName(d);
                    return roman3.IsMatch(name);
                }).ToList();

                for (int x = 0; x < sections.Count; x++)
                {

                    if (Path.Exists(Path.Combine(sections[x], "01_RVT")))
                    {
                        sections[x] = Path.Combine(sections[x], "01_RVT");
                        var files = Directory.GetFiles(sections[x]);
                        foreach (var file in files)
                        {
                            if (file.Contains(".rvt"))
                                allFiles.Add(file);
                        }
                    }


                }

            }
            return allFiles;
        }
    }
}
