using System.Text.RegularExpressions;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotServer.DTOs;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services
{
    public partial class CommandAppService(
        IDataService dataService,
        ITelegramOutputService outputService,
        IFileSystemBrowser fileNavigationService,
        ISessionManager sessionManager,
        IKeyboardBuilder keyboardBuilder,
        IConfiguration configuration,
        ILogger<CommandAppService> logger) : ICommandAppService
    {
        private readonly IDataService _dataService = dataService;
        private readonly ITelegramOutputService _outputService = outputService;
        private readonly IFileSystemBrowser _fileNavigationService = fileNavigationService;
        private readonly IKeyboardBuilder _keyboardBuilder = keyboardBuilder;
        private readonly ISessionManager _sessionManager = sessionManager;
        private readonly ILogger<CommandAppService> _logger = logger;
        private readonly string _rootPath = configuration["TelegramBot:RootPath"] ?? "B:\\";

        public async Task HandleUserCommandAsync(MessageDto message)
        {
            if (message.Username == null || message.Text == null)
            {
                await _outputService.SendMessageAsync(message.UserId, "Username or text is empty.");
                return;
            }

            string text = message.Text;
            DateTime date = message.Date;
            long userId = message.UserId;
            long chatId = message.ChatId;
            int messageId = message.MessageId;
            string username = message.Username;

            _logger.LogInformation("Received command '{Command}' from {Username} ({UserId})", text, username, userId);

            var session = _sessionManager.GetOrCreateSession(userId);

            switch (text.ToLower())
            {
                case "/export":
                    {
                        session.PagesCache.Clear();
                        session.SelectedFiles.Clear();
                        session.PendingCommand.Clear();
                        session.PendingCommandName.Clear();
                        session.CurrentPath = _rootPath;

                        session.Counter = 0;
                        session.Items.Clear();

                        InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetCommandsKeyboardAsync(userId, session);

                        await _outputService.SendMessageWithKeyboardAsync(userId, $"Выберите команду:", keyboard);
                        break;
                    }
                case "/status":
                    {
                        session.PagesCache.Clear();
                        session.SelectedFiles.Clear();
                        session.PendingCommand.Clear();
                        session.PendingCommandName.Clear();
                        session.CurrentPath = _rootPath;

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
                        session.SelectedFiles.Clear();
                        session.PendingCommand.Clear();
                        session.PendingCommandName.Clear();
                        session.CurrentPath = _rootPath;
                        session.PagesCache.Clear();

                        session.Counter = 0;
                        session.Items.Clear();

                        InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetAutomationKeyboardAsync(userId, session);

                        await _outputService.SendMessageWithKeyboardAsync(userId, $"Выберите команду:", keyboard);
                        break;
                    }
                case "/help":
                    session.SelectedFiles.Clear();
                    session.PendingCommand.Clear();
                    session.PendingCommandName.Clear();
                    session.CurrentPath = _rootPath;
                    session.PagesCache.Clear();

                    session.Counter = 0;
                    session.Items.Clear();

                    await _outputService.SendMessageAsync(userId,
                    "/export - используется для экспорта в форматы PDF, DWG, NWC, IFC.\n" +
                    "/automation - используется для автоматизации задач. BIM Doctor, Clash Report, Auto Resolver \n" +
                    "/status - используется для проверки состояния выполнения команды отправленной пользователем.\n" +
                    "При отправке данной команды пользователю будет предоставлени список сессии с временем отправки на обработку.\n" +
                    "Пользователь может нажать на сессию для мониторинга процесса выполнения команды.\n" +
                    "Кроме того в предоставленном меню пользователь может полностью удалить сессию\n"
                    );

                    break;
                default:
                    break;
            }
        }

        public async Task HandleCallbackAsync(CallbackQueryDto callback)
        {
            if (callback.Username == null || callback.MessageText == null || callback.CallbackData == null || callback.CallbackQueryId == null)
            {
                await _outputService.SendMessageAsync(callback.UserId, "Callback is empty.");
                return;
            }

            long chatId = callback.ChatId;
            long userId = callback.UserId;
            int messageId = callback.MessageId;
            string username = callback.Username;
            string callbackData = callback.CallbackData;
            string callbackQueryId = callback.CallbackQueryId;
            List<List<ButtonDto>> buttonDtos = callback.Buttons;
            var parsedCallback = CallbackDataParser.Parse(callbackData);

            var session = _sessionManager.GetOrCreateSession(userId);

            if (session.SelectionType == 1)
            {
if (parsedCallback.IsAny(CallbackPrefixes.OpenFolder, CallbackPrefixes.GoToParent))
                {
                    session.PagesCache.Add(session.Counter);
                    session.Counter = 0;


                    if (parsedCallback.Is(CallbackPrefixes.GoToParent))
                    {
                        if (session.PagesCache.Count > 0)
                            session.PagesCache.RemoveAt(session.PagesCache.Count - 1);
                        session.Counter = session.PagesCache.Count > 0 ? session.PagesCache.Last() : 0;
                    }

                    var token = parsedCallback.Argument;
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
                else if (parsedCallback.Is(CallbackPrefixes.File))
                {



                    var token = parsedCallback.Argument;
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

                else if (parsedCallback.Is(CallbackPrefixes.ApplyFiles))
                {
                    if (session.SelectedFiles.Count > 0)
                    {
                        await _dataService.CreateSessionWithCommandsAsync(session.PendingCommand, session.SelectedFiles, userId, username, session.SelectionType, session.SelectedFiles.Count);

                        await _outputService.EditMessageReplyTextAsync(userId, messageId, BuildQueueReply(session));

                        session.Items.Clear();
                        session.SelectedFiles.Clear();
                        session.PagesCache.Clear();
                        session.SelectionType = 1;
                    }
                }

                else if (parsedCallback.Is(CallbackPrefixes.CancelSelection))
                {
                    session.SelectedFiles.Clear();

                    InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);

                    await _outputService.EditMessageReplyMarkupAsync(
                    userId,
                    messageId,
                    keyboard
                    );
                }
                else if (parsedCallback.Is(CallbackPrefixes.CancelFileSelection))
                {
                    session.SelectedFiles.Clear();
                    session.CurrentPath = _rootPath;
                    session.Counter = 0;
                    session.Items.Clear();
                    session.PagesCache.Clear();
                    session.SelectionType = 1;

                    if (HasExportCommands(session))
                    {
                        InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetCommandsKeyboardAsync(userId, session);
                        await _outputService.EditMessageReplyMarkupAsync(userId, messageId, keyboard);
                    }

                    if (HasAutomationCommands(session))
                    {
                        InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetAutomationKeyboardAsync(userId, session);
                        await _outputService.EditMessageReplyMarkupAsync(userId, messageId, keyboard);
                    }
                }
            }
            else if (session.SelectionType == 2)
            {
if (parsedCallback.IsAny(CallbackPrefixes.OpenFolder, CallbackPrefixes.GoToParent))
                {
                    session.PagesCache.Add(session.Counter);
                    session.Counter = 0;

                    session.Level = true;
                    if (parsedCallback.Is(CallbackPrefixes.GoToParent))
                    {
                        session.Level = false;
                        if (session.PagesCache.Count > 0)
                            session.PagesCache.RemoveAt(session.PagesCache.Count - 1);
                        session.Counter = session.PagesCache.Count > 0 ? session.PagesCache.Last() : 0;
                    }

                    var token = parsedCallback.Argument;
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

if (parsedCallback.Is(CallbackPrefixes.GoToParent))
                    {
                        session.CurrentPath = _rootPath;
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
                else if (parsedCallback.Is(CallbackPrefixes.File))
                {



                    var token = parsedCallback.Argument;
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

                else if (parsedCallback.Is(CallbackPrefixes.ApplyFiles))
                {
                    if (session.SelectedFiles.Count > 0)
                    {
                        session.SelectedFiles = new HashSet<string>(await MapSectionsToFilesAsync(session.SelectedFiles.ToList()));

                        await _dataService.CreateSessionWithCommandsAsync(session.PendingCommand, session.SelectedFiles, userId, username, session.SelectionType, session.SelectedFiles.Count);

                        await _outputService.EditMessageReplyTextAsync(userId, messageId, BuildQueueReply(session));

                        session.SelectedFiles.Clear();
                        session.PagesCache.Clear();
                        session.Level = false;
                        session.SelectionType = 1;
                    }
                }

                else if (parsedCallback.Is(CallbackPrefixes.CancelSelection))
                {
                    session.SelectedFiles.Clear();


                    InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);

                    await _outputService.EditMessageReplyMarkupAsync(
                    userId,
                    messageId,
                    keyboard
                    );
                }
                else if (parsedCallback.Is(CallbackPrefixes.CancelFileSelection))
                {
                    session.SelectedFiles.Clear();
                    session.CurrentPath = _rootPath;
                    session.Level = false;
                    session.PagesCache.Clear();
                    session.SelectionType = 1;

                    if (HasExportCommands(session))
                    {
                        InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetCommandsKeyboardAsync(userId, session);
                        await _outputService.EditMessageReplyMarkupAsync(
                        userId,
                        messageId,
                        keyboard
                        );
                    }

                    if (HasAutomationCommands(session))
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
if (parsedCallback.IsAny(CallbackPrefixes.OpenFolder, CallbackPrefixes.GoToParent))
                {
                    session.PagesCache.Add(session.Counter);
                    session.Counter = 0;

                    if (parsedCallback.Is(CallbackPrefixes.GoToParent))
                    {
                        if (session.PagesCache.Count > 0)
                            session.PagesCache.RemoveAt(session.PagesCache.Count - 1);
                        session.Counter = session.PagesCache.Count > 0 ? session.PagesCache.Last() : 0;
                    }

                    var token = parsedCallback.Argument;
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

                    session.CurrentPath = newPath;

                    await _outputService.AnswerCallbackAsync(callbackQueryId, session.CurrentPath);

                    InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);
                    await _outputService.EditMessageReplyMarkupAsync(userId, messageId, keyboard);
                }
                else if (parsedCallback.Is(CallbackPrefixes.File))
                {



                    var token = parsedCallback.Argument;
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

                else if (parsedCallback.Is(CallbackPrefixes.ApplyFiles))
                {
                    if (session.SelectedFiles.Count > 0)
                    {

                        session.SelectedFiles = new HashSet<string>(await MapProjectsToFilesAsync(session.SelectedFiles.ToList()));



                        await _dataService.CreateSessionWithCommandsAsync(session.PendingCommand, session.SelectedFiles, userId, username, session.SelectionType, session.SelectedFiles.Count);

                        await _outputService.EditMessageReplyTextAsync(userId, messageId, BuildQueueReply(session));

                        session.SelectedFiles.Clear();
                        session.PagesCache.Clear();
                        session.SelectionType = 1;
                    }
                }

                else if (parsedCallback.Is(CallbackPrefixes.CancelSelection))
                {
                    session.SelectedFiles.Clear();


                    InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);

                    await _outputService.EditMessageReplyMarkupAsync(
                    userId,
                    messageId,
                    keyboard
                    );
                }
                else if (parsedCallback.Is(CallbackPrefixes.CancelFileSelection))
                {
                    session.SelectedFiles.Clear();
                    session.CurrentPath = _rootPath;
                    session.PagesCache.Clear();
                    session.SelectionType = 1;

                    if (HasExportCommands(session))
                    {
                        InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetCommandsKeyboardAsync(userId, session);
                        await _outputService.EditMessageReplyMarkupAsync(
                        userId,
                        messageId,
                        keyboard
                        );
                    }

                    if (HasAutomationCommands(session))
                    {
                        InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetAutomationKeyboardAsync(userId, session);
                        await _outputService.EditMessageReplyMarkupAsync(
                        userId,
                        messageId,
                        keyboard);
                    }

                }
            }


            if (parsedCallback.Is(CallbackPrefixes.SelectionMode))
            {
                int nextMode = session.SelectionType switch
                {
                    1 => 2,
                    2 => 3,
                    3 => 1,
                    _ => 1
                };
                session.SelectionType = nextMode;
                session.CurrentPath = _rootPath;
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

            else if (parsedCallback.Is(CallbackPrefixes.Pdf))
            {
                await TogglePendingCommandAsync(userId, messageId, session, "PDF", "Export to PDF", isAutomation: false);
            }

            else if (parsedCallback.Is(CallbackPrefixes.Dwg))
            {
                await TogglePendingCommandAsync(userId, messageId, session, "DWG", "Export to DWG", isAutomation: false);
            }

            else if (parsedCallback.Is(CallbackPrefixes.Nwc))
            {
                await TogglePendingCommandAsync(userId, messageId, session, "NWC", "Export to NWC", isAutomation: false);
            }

            else if (parsedCallback.Is(CallbackPrefixes.Ifc))
            {
                await TogglePendingCommandAsync(userId, messageId, session, "IFC", "Export to IFC", isAutomation: false);
            }

            else if (parsedCallback.Is(CallbackPrefixes.ApplyCommands))
            {
                if (session.PendingCommand.Count > 0)
                {
                    session.CurrentPath = _rootPath;
                    InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);
                    await _outputService.EditMessageReplyMarkupAsync(userId, messageId, keyboard);
                }

            }

            else if (parsedCallback.Is(CallbackPrefixes.CancelCommandSelection))
            {
                session.PendingCommand.Clear();
                session.PendingCommandName.Clear();
                await _outputService.DeleteMessageAsync(chatId, messageId);
            }

            else if (parsedCallback.Is(CallbackPrefixes.SessionDetails) && session.statusLevel == true)
            {
                session.statusLevel = false;

                var token = parsedCallback.Argument;

                int.TryParse(token, out int tkn);

                session.sessionId = tkn;

                SessionStatus sessionStatus = await _dataService.GetSessionsStatusAsync(tkn);

                int percentage = sessionStatus.TotalFiles > 0
                    ? (100 * sessionStatus.DoneFiles) / sessionStatus.TotalFiles
                    : 0;

                string reply = $"Статус: {sessionStatus.Status}\nФайлов: {sessionStatus.TotalFiles}\nЗавершено: {sessionStatus.DoneFiles}\n{percentage}%";

                await _outputService.EditMessageReplyTextAsync(userId, messageId, reply);

                InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSessionStatusKeyboardAsync(sessionStatus, tkn);

                await _outputService.EditMessageReplyMarkupAsync(userId, messageId, keyboard);
            }

            else if (parsedCallback.Is(CallbackPrefixes.SessionDetails) && session.statusLevel == false)
            {
                session.statusLevel = true;

                var token = parsedCallback.Argument;

                int.TryParse(token, out int tkn);

                List<SessionCommands> sessionCommands = await _dataService.GetSessionsCommandsAsync(tkn);

                InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSessionCommandsKeyboardAsync(sessionCommands, tkn);
                await _outputService.EditMessageReplyMarkupAsync(
                userId,
                messageId,
                keyboard
                );
            }

            else if (parsedCallback.Is(CallbackPrefixes.BackToStatus))
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

            else if (parsedCallback.Is(CallbackPrefixes.DeleteSession))
            {
                var token = parsedCallback.Argument;
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

            else if (parsedCallback.Is(CallbackPrefixes.DeleteCommand))
            {
                var token = parsedCallback.Argument;

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

                        int percentage = sessionStatus.TotalFiles > 0
                            ? (100 * sessionStatus.DoneFiles) / sessionStatus.TotalFiles
                            : 0;

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
            else if (parsedCallback.Is(CallbackPrefixes.BimDoc))
            {
                await TogglePendingCommandAsync(userId, messageId, session, "BIMDOC", "BIMDOC", isAutomation: true);
            }
            else if (parsedCallback.Is(CallbackPrefixes.ClashRep))
            {
                await TogglePendingCommandAsync(userId, messageId, session, "CLASHREP", "CLASHREP", isAutomation: true);
            }
            else if (parsedCallback.Is(CallbackPrefixes.AutoRes))
            {
                await TogglePendingCommandAsync(userId, messageId, session, "AUTORES", "AUTORES", isAutomation: true);
            }
            else
            {
                _logger.LogDebug("Unhandled callback data '{CallbackData}' for user {UserId}", callbackData, userId);
            }
        }

        private static string BuildQueueReply(UserSession session)
        {
            var sb = new System.Text.StringBuilder("Команда:\n");
            foreach (var file in session.PendingCommandName)
                sb.Append("\u2705 ").Append(Path.GetFileName(file)).Append('\n');
            sb.Append("Добавлены файлы:\n");
            foreach (var file in session.SelectedFiles)
                sb.Append("\u2705 ").Append(Path.GetFileName(file)).Append('\n');
            sb.Append("\n/status для проверки статуса команды");
            return sb.ToString();
        }

        private async Task TogglePendingCommandAsync(
            long userId,
            int messageId,
            UserSession session,
            string commandCode,
            string commandDisplayName,
            bool isAutomation)
        {
            if (session.PendingCommand.Contains(commandCode))
            {
                session.PendingCommand.Remove(commandCode);
                session.PendingCommandName.Remove(commandDisplayName);
            }
            else
            {
                session.PendingCommand.Add(commandCode);
                session.PendingCommandName.Add(commandDisplayName);
            }

            InlineKeyboardMarkup keyboard = isAutomation
                ? await _keyboardBuilder.GetAutomationKeyboardAsync(userId, session)
                : await _keyboardBuilder.GetCommandsKeyboardAsync(userId, session);

            await _outputService.EditMessageReplyMarkupAsync(userId, messageId, keyboard);
        }

        private static bool HasExportCommands(UserSession session)
        {
            return session.PendingCommand.Contains("PDF")
                || session.PendingCommand.Contains("DWG")
                || session.PendingCommand.Contains("NWC")
                || session.PendingCommand.Contains("IFC");
        }

        private static bool HasAutomationCommands(UserSession session)
        {
            return session.PendingCommand.Contains("BIMDOC")
                || session.PendingCommand.Contains("CLASHREP")
                || session.PendingCommand.Contains("AUTORES");
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

        public Task<List<string>> MapSectionsToFilesAsync(List<string> dirs)
        {
            return Task.Run(() =>
            {
                var allFiles = new List<string>();
                foreach (var dir in dirs)
                {
                    var rvtDir = Path.Combine(dir, "01_RVT");
                    foreach (var file in Directory.GetFiles(rvtDir))
                    {
                        if (file.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
                            allFiles.Add(file);
                    }
                }
                return allFiles;
            });
        }

        public Task<List<string>> MapProjectsToFilesAsync(List<string> dirs)
        {
            Regex roman3 = MyRegex();
            return Task.Run(() =>
            {
                var allFiles = new List<string>();
                foreach (var dir in dirs)
                {
                    var projectDir = Path.Combine(dir, "01_PROJECT");
                    var sections = Directory.GetDirectories(projectDir)
                        .Where(d => roman3.IsMatch(Path.GetFileName(d)))
                        .ToList();

                    foreach (var section in sections)
                    {
                        var rvtDir = Path.Combine(section, "01_RVT");
                        if (Path.Exists(rvtDir))
                        {
                            foreach (var file in Directory.GetFiles(rvtDir))
                            {
                                if (file.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
                                    allFiles.Add(file);
                            }
                        }
                    }
                }
                return allFiles;
            });
        }

        [GeneratedRegex(@"^III_", RegexOptions.IgnoreCase, "ru-KZ")]
        private static partial Regex MyRegex();
    }
}
