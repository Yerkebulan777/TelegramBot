using System.Text;
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
            long userId = message.UserId;
            string username = message.Username;

            _logger.LogInformation("Received command '{Command}' from {Username} ({UserId})", text, username, userId);

            var session = _sessionManager.GetOrCreateSession(userId);

            switch (text.ToLower())
            {
                case "/export":
                    {
                        session.Reset(_rootPath);

                        InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetCommandsKeyboardAsync(userId, session);
                        await _outputService.SendMessageWithKeyboardAsync(userId, $"Выберите команду:", keyboard);
                        break;
                    }
                case "/status":
                    {
                        session.Reset(_rootPath);
                        session.StatusLevel = true;

                        List<SessionsList> sessionsStatus = await _dataService.GetSessionsListAsync(userId);
                        InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSessionsListKeyboardAsync(sessionsStatus);
                        await _outputService.SendMessageWithKeyboardAsync(userId, $"Сессии:", keyboard);
                        break;
                    }
                case "/automation":
                    {
                        session.Reset(_rootPath);

                        InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetAutomationKeyboardAsync(userId, session);
                        await _outputService.SendMessageWithKeyboardAsync(userId, $"Выберите команду:", keyboard);
                        break;
                    }
                case "/help":
                    session.Reset(_rootPath);

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

            // --- Selection-mode-specific navigation & file operations ---
            if (session.SelectionType is SelectionMode.Files or SelectionMode.Sections or SelectionMode.Projects)
            {
                if (parsedCallback.IsAny(CallbackPrefixes.OpenFolder, CallbackPrefixes.GoToParent))
                {
                    await HandleFolderNavigationAsync(userId, messageId, callbackQueryId, session, parsedCallback);
                    return;
                }
                else if (parsedCallback.Is(CallbackPrefixes.File))
                {
                    await HandleFileToggleAsync(userId, messageId, session, parsedCallback);
                    return;
                }
                else if (parsedCallback.Is(CallbackPrefixes.ApplyFiles))
                {
                    await HandleApplyFilesAsync(userId, messageId, username, session);
                    return;
                }
                else if (parsedCallback.Is(CallbackPrefixes.CancelSelection))
                {
                    await HandleCancelSelectionAsync(userId, messageId, session);
                    return;
                }
                else if (parsedCallback.Is(CallbackPrefixes.CancelFileSelection))
                {
                    await HandleCancelFileSelectionAsync(userId, messageId, session);
                    return;
                }
            }

            // --- Global callbacks (not dependent on selection type) ---
            if (parsedCallback.Is(CallbackPrefixes.SelectionMode))
            {
                var nextMode = session.SelectionType switch
                {
                    SelectionMode.Files => SelectionMode.Sections,
                    SelectionMode.Sections => SelectionMode.Projects,
                    SelectionMode.Projects => SelectionMode.Files,
                    _ => SelectionMode.Files
                };
                session.SelectionType = nextMode;
                session.CurrentPath = _rootPath;
                session.Level = false;
                session.SelectedFiles.Clear();
                session.Counter = 0;
                session.PagesCache.Clear();

                InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);
                await _outputService.EditMessageReplyMarkupAsync(userId, messageId, keyboard);
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
            else if (parsedCallback.Is(CallbackPrefixes.SessionDetails))
            {
                await HandleSessionDetailsAsync(userId, messageId, session, parsedCallback);
            }
            else if (parsedCallback.Is(CallbackPrefixes.BackToStatus))
            {
                session.StatusLevel = true;
                List<SessionsList> sessionsStatus = await _dataService.GetSessionsListAsync(userId);
                InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSessionsListKeyboardAsync(sessionsStatus);
                await _outputService.EditMessageTextWithKeyboardAsync(userId, messageId, "Сессии:", keyboard);
            }
            else if (parsedCallback.Is(CallbackPrefixes.DeleteSession))
            {
                await HandleDeleteSessionAsync(userId, messageId, session, parsedCallback);
            }
            else if (parsedCallback.Is(CallbackPrefixes.DeleteCommand))
            {
                await HandleDeleteCommandAsync(userId, messageId, session, parsedCallback, buttonDtos);
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

        // ========== Extracted handler methods to eliminate duplication ==========

        private async Task HandleFolderNavigationAsync(
            long userId, int messageId, string callbackQueryId,
            UserSession session, ParsedCallback parsedCallback)
        {
            session.PagesCache.Add(session.Counter);
            session.Counter = 0;

            bool isGoToParent = parsedCallback.Is(CallbackPrefixes.GoToParent);

            if (session.SelectionType == SelectionMode.Sections)
            {
                session.Level = !isGoToParent;
            }

            if (isGoToParent)
            {
                if (session.PagesCache.Count > 0)
                    session.PagesCache.RemoveAt(session.PagesCache.Count - 1);
                session.Counter = session.PagesCache.Count > 0 ? session.PagesCache.Last() : 0;
            }

            var token = parsedCallback.Argument;
            if (!_fileNavigationService.TryResolvePath(userId, token, out var newPath) || newPath == null)
            {
                await _outputService.SendErrorAsync(userId, "Path not found.");
                return;
            }

            // Selection-type-specific path logic
            if (session.SelectionType == SelectionMode.Sections)
            {
                if (isGoToParent)
                {
                    session.CurrentPath = _rootPath;
                }
                else
                {
                    session.CurrentPath = newPath + "\\01_PROJECT";
                }
            }
            else
            {
                session.CurrentPath = newPath;
            }

            await _outputService.AnswerCallbackAsync(callbackQueryId, session.CurrentPath);

            InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);
            await _outputService.EditMessageReplyMarkupAsync(userId, messageId, keyboard);
        }

        private async Task HandleFileToggleAsync(
            long userId, int messageId,
            UserSession session, ParsedCallback parsedCallback)
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
            await _outputService.EditMessageReplyMarkupAsync(userId, messageId, keyboard);
        }

        private async Task HandleApplyFilesAsync(
            long userId, int messageId, string username, UserSession session)
        {
            if (session.SelectedFiles.Count == 0)
                return;

            // Map selection to actual files based on selection type
            if (session.SelectionType == SelectionMode.Sections)
            {
                session.SelectedFiles = new HashSet<string>(
                    await MapSectionsToFilesAsync(session.SelectedFiles.ToList()));
            }
            else if (session.SelectionType == SelectionMode.Projects)
            {
                session.SelectedFiles = new HashSet<string>(
                    await MapProjectsToFilesAsync(session.SelectedFiles.ToList()));
            }

            await _dataService.CreateSessionWithCommandsAsync(
                session.PendingCommand, session.SelectedFiles,
                userId, username,
                (int)session.SelectionType, session.SelectedFiles.Count);

            await _outputService.EditMessageReplyTextAsync(userId, messageId, BuildQueueReply(session));

            session.Items.Clear();
            session.SelectedFiles.Clear();
            session.PagesCache.Clear();
            session.Level = false;
            session.SelectionType = SelectionMode.Files;
        }

        private async Task HandleCancelSelectionAsync(
            long userId, int messageId, UserSession session)
        {
            session.SelectedFiles.Clear();

            InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(userId, session);
            await _outputService.EditMessageReplyMarkupAsync(userId, messageId, keyboard);
        }

        private async Task HandleCancelFileSelectionAsync(
            long userId, int messageId, UserSession session)
        {
            session.SelectedFiles.Clear();
            session.CurrentPath = _rootPath;
            session.Counter = 0;
            session.Items.Clear();
            session.PagesCache.Clear();
            session.Level = false;
            session.SelectionType = SelectionMode.Files;

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

        private async Task HandleSessionDetailsAsync(
            long userId, int messageId, UserSession session, ParsedCallback parsedCallback)
        {
            var token = parsedCallback.Argument;
            if (!int.TryParse(token, out int tkn))
            {
                _logger.LogWarning("Invalid session ID '{Token}' from user {UserId}", token, userId);
                return;
            }

            if (session.StatusLevel)
            {
                // Show session summary
                session.StatusLevel = false;
                session.SessionId = tkn;

                SessionStatus sessionStatus = await _dataService.GetSessionsStatusAsync(tkn);

                int percentage = sessionStatus.TotalFiles > 0
                    ? (100 * sessionStatus.DoneFiles) / sessionStatus.TotalFiles
                    : 0;

                string reply = $"Статус: {sessionStatus.Status}\nФайлов: {sessionStatus.TotalFiles}\nЗавершено: {sessionStatus.DoneFiles}\n{percentage}%";

                InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSessionStatusKeyboardAsync(sessionStatus, tkn);
                await _outputService.EditMessageTextWithKeyboardAsync(userId, messageId, reply, keyboard);
            }
            else
            {
                // Show session commands detail
                session.StatusLevel = true;

                List<SessionCommands> sessionCommands = await _dataService.GetSessionsCommandsAsync(tkn);
                InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSessionCommandsKeyboardAsync(sessionCommands, tkn);
                await _outputService.EditMessageReplyMarkupAsync(userId, messageId, keyboard);
            }
        }

        private async Task HandleDeleteSessionAsync(
            long userId, int messageId, UserSession session, ParsedCallback parsedCallback)
        {
            var token = parsedCallback.Argument;
            if (!int.TryParse(token, out int tkn))
            {
                _logger.LogWarning("Invalid session ID '{Token}' for deletion from user {UserId}", token, userId);
                return;
            }

            if (await _dataService.DeleteSessionAsync(tkn))
            {
                session.StatusLevel = true;
                List<SessionsList> sessionsStatus = await _dataService.GetSessionsListAsync(userId);
                InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSessionsListKeyboardAsync(sessionsStatus);
                await _outputService.EditMessageTextWithKeyboardAsync(userId, messageId, "Сессии:", keyboard);
            }
        }

        private async Task HandleDeleteCommandAsync(
            long userId, int messageId, UserSession session,
            ParsedCallback parsedCallback, List<List<ButtonDto>> buttonDtos)
        {
            var token = parsedCallback.Argument;
            if (!int.TryParse(token, out int tkn))
            {
                _logger.LogWarning("Invalid command ID '{Token}' for deletion from user {UserId}", token, userId);
                return;
            }

            if (!await _dataService.DeleteCommandAsync(tkn))
                return;

            foreach (var row in buttonDtos)
            {
                row.RemoveAll(btn => btn.CallbackData!.Contains($"{tkn}"));
            }

            var newKeyboard = ConvertDtoToKeyboard(buttonDtos);

            // Check if all files are deleted; if yes, mark session as deleted and return to sessions page
            if (!await _dataService.CheckCommandsStatusAsync(session.SessionId))
            {
                if (await _dataService.DeleteSessionAsync(session.SessionId))
                {
                    session.StatusLevel = true;
                    List<SessionsList> sessionsStatus = await _dataService.GetSessionsListAsync(userId);
                    InlineKeyboardMarkup keyboard = await _keyboardBuilder.GetSessionsListKeyboardAsync(sessionsStatus);
                    await _outputService.EditMessageTextWithKeyboardAsync(userId, messageId, "Сессии:", keyboard);
                }
            }
            else
            {
                SessionStatus sessionStatus = await _dataService.GetSessionsStatusAsync(session.SessionId);

                int percentage = sessionStatus.TotalFiles > 0
                    ? (100 * sessionStatus.DoneFiles) / sessionStatus.TotalFiles
                    : 0;

                string reply = $"Статус: {sessionStatus.Status}\nФайлов: {sessionStatus.TotalFiles}\nЗавершено: {sessionStatus.DoneFiles}\n{percentage}%";

                await _outputService.EditMessageTextWithKeyboardAsync(userId, messageId, reply, newKeyboard);
            }
        }

        // ========== Helper methods ==========

        private static string BuildQueueReply(UserSession session)
        {
            var sb = new StringBuilder("Команда:\n");
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
                    if (!Directory.Exists(rvtDir))
                    {
                        _logger.LogWarning("RVT directory not found: {RvtDir}", rvtDir);
                        continue;
                    }

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
                    if (!Directory.Exists(projectDir))
                    {
                        _logger.LogWarning("Project directory not found: {ProjectDir}", projectDir);
                        continue;
                    }

                    var sections = Directory.GetDirectories(projectDir)
                        .Where(d => roman3.IsMatch(Path.GetFileName(d)))
                        .ToList();

                    foreach (var section in sections)
                    {
                        var rvtDir = Path.Combine(section, "01_RVT");
                        if (Directory.Exists(rvtDir))
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
