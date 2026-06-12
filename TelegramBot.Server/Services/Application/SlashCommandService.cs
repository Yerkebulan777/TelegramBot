using Microsoft.Extensions.Options;
using System.Collections.Frozen;
using System.Text;
using System.Text.RegularExpressions;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.DTOs;
using TelegramBot.Core.Models;
using TelegramBot.Server.Helpers;
using TelegramBot.Server.Interfaces;
using TelegramBot.Server.Middleware;
using TelegramBot.Server.Models;
using TelegramBot.Server.Services.Application.Handlers;
using TelegramBot.Server.Services.Infrastructure.Telegram;
using Message = Telegram.Bot.Types.Message;

namespace TelegramBot.Server.Services.Application;

public sealed partial class SlashCommandService(
    DataServices dataServices,
    ITelegramOutputService outputService,
    KeyboardBuilder keyboardBuilder,
    AuthorizationMiddleware accessValidator,
    IOptions<FileSystemOptions> fileSystemOptions,
    IOptions<RateLimitOptions> rateLimitOptions,
    ILogger<SlashCommandService> logger)
{
    private static readonly FrozenDictionary<string, int> _commandPriorityMap =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["PDF"] = CommandPriorities.Critical,
            ["DWG"] = CommandPriorities.High,
            ["NWC"] = CommandPriorities.Medium,
            ["IFC"] = CommandPriorities.Medium,
            ["BIMDOC"] = CommandPriorities.Medium,
            ["CLASHREP"] = CommandPriorities.Medium,
            ["AUTORES"] = CommandPriorities.Low,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private readonly FileSystemOptions _options = fileSystemOptions.Value;
    private readonly RateLimitOptions _rateLimitOptions = rateLimitOptions.Value;

    [GeneratedRegex(@"(?:^|[_ -])[BSCPKITGM]+\d*[_ -][ASRPGJOVIK]+\d*", RegexOptions.IgnoreCase)]
    private static partial Regex RvtSectionPattern();

    private const long _rvtMinFileSizeBytes = 50L * 1024 * 1024;

    private static readonly EnumerationOptions _rvtEnumOptions = new()
    {
        RecurseSubdirectories = true,
        MaxRecursionDepth = 3,
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public async Task HandleUserCommandAsync(MessageDto message, UserSession session, CancellationToken cancellationToken = default)
    {
        var context = await ValidateUserContextAsync(message.UserId, message.Text!, message, session);
        var strategy = ResolveCommandStrategy(context);
        var result = await ExecuteBusinessLogicAsync(context, strategy, cancellationToken);
        var responseMessage = await FormatResponseMessageAsync(result);

        await SendSafeResponseAsync(context.ChatId, responseMessage, context.Session);
        await LogCommandExecutionAsync(context, strategy, result);
    }

    private async Task HandleSlashCommandAsync(string command, MessageDto message, UserSession session, string username)
    {
        var userId = message.UserId;
        var isSlashCommand = command.StartsWith('/');

        switch (command)
        {
            case "/export":
                logger.LogDebug("Executing /export for {Username} ({UserId})", username, userId);
                await StartCommandSelectionAsync(userId, session, CommandGroup.Export);
                break;

            case "/status":
                logger.LogDebug("Executing /status for {Username} ({UserId})", username, userId);
                session.Reset(_options.RootPath);
                session.IsInStatusView = true;
                session.StatusFilter = "ALL";
                session.StatusPage = 1;

                var pageSize = keyboardBuilder.DefaultPageSize;
                var sessionsStatus = await dataServices.Sessions.GetSessionsListFilteredAsync(
                    session.StatusFilter, session.StatusPage, pageSize);
                var totalCount = await dataServices.Sessions.CountSessionsFilteredAsync(session.StatusFilter);
                var totalPages = Math.Max(1, (int)Math.Ceiling((double)totalCount / pageSize));

                var messageText = $"📋 Все сессии — стр. 1/{totalPages} (всего {totalCount})";
                var keyboard = await keyboardBuilder.GetSessionsListKeyboardAsync(
                    sessionsStatus, session.StatusFilter, session.StatusPage, totalPages);
                var statusMessage = await TrackMessageAsync(outputService.SendMessageWithKeyboardAsync(userId, messageText, keyboard), session);
                session.StatusMessageId = statusMessage?.Id;
                break;

            case "/automation":
                logger.LogDebug("Executing /automation for {Username} ({UserId})", username, userId);
                await StartCommandSelectionAsync(userId, session, CommandGroup.Automation);
                break;

            case "/help":
                logger.LogDebug("Executing /help for {Username} ({UserId})", username, userId);
                session.Reset(_options.RootPath);
                await SendHelpMessageAsync(userId, session);
                break;

            default:
                if (isSlashCommand)
                {
                    logger.LogWarning("Unknown slash command '{Command}' from {Username} ({UserId})", command, username, userId);
                }
                else
                {
                    logger.LogDebug("Ignoring non-command text from {Username} ({UserId})", username, userId);
                }

                break;
        }
    }

    private async Task<UserCommandContext> ValidateUserContextAsync(long userId, string command, MessageDto message, UserSession session)
    {
        var username = message.Username!;

        var text = NormalizeCommandText(command);

        ArgumentNullException.ThrowIfNullOrWhiteSpace(username);

        logger.LogDebug("Command received: command={Command}, user={Username} ({UserId})", text, username, userId);

        var access = await accessValidator.ValidateAsync(userId);
        var user = access.User;
        if (text == "/start" && !access.IsActive)
        {
            _ = await accessValidator.RefreshApprovedAdminUserAsync(userId, username, user);
            access = await accessValidator.ValidateAsync(userId);
            user = access.User;
        }

        return new UserCommandContext(
            message,
            session,
            userId,
            message.ChatId == 0 ? userId : message.ChatId,
            command,
            text,
            username,
            user,
            text == "/start" || access.HasAccess);
    }

    private static CommandStrategy ResolveCommandStrategy(UserCommandContext context)
    {
        return context.HasAccess switch
        {
            false => CommandStrategy.AccessDenied,
            true => context.Command switch
            {
                "/start" => CommandStrategy.Start,
                _ when IsCommandSelectionAction(context.RawText) => CommandStrategy.CommandSelectionAction,
                _ => CommandStrategy.SlashCommand
            }
        };
    }

    private async Task<CommandExecutionResult> ExecuteBusinessLogicAsync(
        UserCommandContext context,
        CommandStrategy strategy,
        CancellationToken cancellationToken)
    {
        if (context.Command.StartsWith('/'))
        {
            await outputService.ClearChatHistoryAsync(context.UserId, context.Session);
        }

        switch (strategy)
        {
            case CommandStrategy.AccessDenied:
                return CommandExecutionResult.AccessDenied();

            case CommandStrategy.Start:
                context.Session.Reset(_options.RootPath);
                if (context.HasAccess)
                {
                    await SendHelpMessageAsync(context.UserId, context.Session);
                }
                else
                {
                    await SendRegistrationMessageAsync(context.UserId, context.Session);
                }

                return CommandExecutionResult.Handled();

            case CommandStrategy.CommandSelectionAction:
                var handled = await HandleCommandSelectionActionsAsync(
                    context.UserId,
                    context.Username,
                    context.RawText,
                    context.Session,
                    cancellationToken);

                if (handled)
                {
                    return CommandExecutionResult.Handled();
                }

                await HandleSlashCommandAsync(context.Command, context.Message, context.Session, context.Username);
                return CommandExecutionResult.Handled();

            case CommandStrategy.SlashCommand:
                await HandleSlashCommandAsync(context.Command, context.Message, context.Session, context.Username);
                return CommandExecutionResult.Handled();

            default:
                throw new InvalidOperationException($"Unknown command strategy '{strategy}'.");
        }
    }

    private static Task<string?> FormatResponseMessageAsync(CommandExecutionResult result)
    {
        return Task.FromResult(result.Status == CommandExecutionStatus.AccessDenied
            ? "У вас нет доступа. Введите /start для запроса доступа."
            : null);
    }

    private async Task SendSafeResponseAsync(long chatId, string? message, UserSession session)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        try
        {
            _=await TrackMessageAsync(outputService.SendMessageAsync(chatId, message), session);
        }
        catch (ApiRequestException ex)
        {
            logger.LogWarning(ex, "Failed to send command response to {ChatId}", chatId);
        }
    }

    private Task LogCommandExecutionAsync(
        UserCommandContext context,
        CommandStrategy strategy,
        CommandExecutionResult result)
    {
        if (result.Status == CommandExecutionStatus.AccessDenied)
        {
            logger.LogWarning(
                "Command rejected: command={Command}, user={Username} ({UserId}), reason=access_denied",
                context.Command,
                context.Username,
                context.UserId);
        }
        else
        {
            logger.LogDebug(
                "Command handled: command={Command}, user={Username} ({UserId}), strategy={Strategy}",
                context.Command,
                context.Username,
                context.UserId,
                strategy);
        }

        return Task.CompletedTask;
    }

    private static bool IsCommandSelectionAction(string messageText)
    {
        return messageText is ButtonTexts.Apply or
            ButtonTexts.Cancel or
            ButtonTexts.Confirm;
    }

    private async Task<bool> HandleCommandSelectionActionsAsync(long userId, string username, string messageText, UserSession session, CancellationToken cancellationToken)
    {
        switch (messageText)
        {
            case ButtonTexts.Apply:
            case ButtonTexts.Confirm when !session.IsFileSelectionActive && session.PendingCommand.Count > 0:
                await ApplyCommandSelectionAsync(userId, username, session);
                return true;

            case ButtonTexts.Confirm when session.IsFileSelectionActive:
                await ConfirmFileSelectionAsync(userId, username, session, cancellationToken);
                return true;

            // Stale/duplicate Confirm press: first press already reset session state
            // (IsFileSelectionActive=false, PendingCommand empty). The press still arrives
            // as a tracked user text message but matches no action above. Delete it so it
            // does not leak in the UI, instead of falling through to default (returns false,
            // no cleanup).
            case ButtonTexts.Confirm:
                logger.LogDebug("Stale Confirm press from {Username} ({UserId}); cleaning orphaned message", username, userId);
                if (session.LastUserMessageId.HasValue)
                {
                    try
                    {
                        await outputService.DeleteMessageAsync(userId, session.LastUserMessageId.Value);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to delete stale Confirm message for user {UserId}", userId);
                    }
                    session.LastUserMessageId = null;
                }
                return true;

            case ButtonTexts.Cancel:
                logger.LogDebug("User {Username} ({UserId}) cancelled active selection", username, userId);

                await outputService.ClearChatHistoryAsync(userId, session);
                session.Reset(_options.RootPath);

                await SendHelpMessageAsync(userId, session);

                return true;

            default:
                return false;
        }
    }

    private async Task ApplyCommandSelectionAsync(long userId, string username, UserSession session)
    {
        if (session.PendingCommand.Count == 0)
        {
            logger.LogDebug("User {Username} ({UserId}) tried to apply with no commands selected", username, userId);
            await SendWarningAndCleanupAsync(userId, session, "Сначала выберите хотя бы одну команду.");
            return;
        }

        logger.LogDebug("User {Username} ({UserId}) confirmed command selection: [{Commands}], opening file browser", username, userId, string.Join(", ", session.PendingCommand));

        await outputService.ClearChatHistoryAsync(userId, session);

        session.CommandSelectionMessageId = null;
        session.CurrentPath = _options.RootPath;
        session.IsFileSelectionActive = true;

        var keyboard = await keyboardBuilder.GetSelectionKeyboardAsync(userId, session);
        var selectionMessage = await TrackMessageAsync(outputService.SendMessageWithKeyboardAsync(userId, "Выберите папки:", keyboard), session);
        session.FileSelectionMessageId = selectionMessage?.Id;
        await SendFileActionsReplyKeyboardAsync(userId, session);
    }

    private async Task ConfirmFileSelectionAsync(long userId, string username, UserSession session, CancellationToken cancellationToken)
    {
        if (!session.FileSelectionMessageId.HasValue)
        {
            logger.LogWarning("Job submit blocked: user={Username} ({UserId}), reason=missing_file_selection_message", username, userId);
            await SendWarningAndCleanupAsync(userId, session, "Сообщение выбора файлов не найдено.");
            return;
        }

        if (_options.IsAtProjectLevel(session.CurrentPath))
        {
            var selectedProject = session.GetSelectedFiles().FirstOrDefault();
            if (selectedProject == null)
            {
                logger.LogDebug("Project confirm blocked: user={Username} ({UserId}), reason=no_project_selected", username, userId);
                await SendWarningAndCleanupAsync(userId, session, "⚠️ Сначала выберите проект.");
                return;
            }

            session.CurrentPath = Path.Combine(selectedProject, _options.ProjectDirectoryName);

            session.ClearSelectedFiles();

            logger.LogDebug("User {Username} ({UserId}) confirmed project '{Project}', navigated to 01_PROJECT", username, userId, Path.GetFileName(selectedProject));

            var keyboard = await keyboardBuilder.GetSelectionKeyboardAsync(userId, session);
            await outputService.EditMessageReplyMarkupAsync(userId, session.FileSelectionMessageId.Value, keyboard);
            await SendFileActionsReplyKeyboardAsync(userId, session);
            await CleanupCurrentViewAsync(userId, session);
            return;
        }

        var selectedSections = session.GetSelectedFiles();
        if (selectedSections.Count == 0)
        {
            logger.LogDebug("Job submit blocked: user={Username} ({UserId}), reason=no_sections_selected", username, userId);
            await SendWarningAndCleanupAsync(userId, session, "⚠️ Сначала выберите хотя бы один раздел.");
            return;
        }

        logger.LogDebug(
            "Job submit: user={Username} ({UserId}), commands={CommandCount}, sections={SectionCount}",
            username, userId, session.PendingCommand.Count, selectedSections.Count);

        // Remove reply keyboard immediately so the user cannot re-press Confirm.
        // Tracked so ClearChatHistoryAsync removes it together with the rest.
        try
        {
            _ = await TrackMessageAsync(outputService.RemoveReplyKeyboardAsync(userId, "…"), session);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove reply keyboard: user={Username} ({UserId})", username, userId);
        }

        var commandNames = session.PendingCommandName;
        var projectName = GetCurrentProjectName(session);
        var sectionNames = selectedSections
            .Select(GetSafePathName)
            .ToArray();

        using var typingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var typingTask = TypingLoopAsync(userId, typingCts.Token);

        try
        {
            var filesToProcess = await CollectRvtFilesAsync(selectedSections, cancellationToken);
            if (filesToProcess.Count == 0)
            {
                logger.LogWarning("Job submit blocked: user={Username} ({UserId}), reason=no_files_found", username, userId);
                await SendWarningAndCleanupAsync(userId, session, "⚠️ В выбранных разделах не найдены файлы для обработки.");
                return;
            }

            if (!await CheckDailyFileLimitAsync(userId, username, session, filesToProcess.Count))
            {
                return;
            }

            // Проверяем, нет ли уже таких же (команда + файл) в очереди
            if (await dataServices.Commands.HasDuplicateCommandsAsync(session.PendingCommand, filesToProcess))
            {
                logger.LogWarning("Job blocked: user={Username} ({UserId}), reason=duplicate_commands_in_queue", username, userId);
                await SendWarningAndCleanupAsync(userId, session, "⚠️ Эти файлы уже в очереди выполнения.");
                return;
            }

            var queuedMessage = BuildJobQueuedMessage(commandNames, projectName, sectionNames, filesToProcess.Count);

            var priorities = session.PendingCommand
                .Select(c => _commandPriorityMap.TryGetValue(c, out var p) ? p : CommandPriorities.Default);

            var correlationId = Guid.NewGuid().ToString("N");
            var sessionId = await dataServices.Sessions.CreateSessionWithCommandsAsync(
                session.PendingCommand, filesToProcess, userId, username, filesToProcess.Count, projectName, priorities, correlationId);
            logger.LogInformation(
                "Job queued: session={SessionId}, correlationId={CorrelationId}, user={Username} ({UserId}), commands={CommandCount}, files={FileCount}",
                sessionId, correlationId, username, userId, session.PendingCommand.Count, filesToProcess.Count);

            session.SessionId = checked((int)sessionId);
            await outputService.ClearChatHistoryAsync(userId, session);

            session.ResetNavigation(_options.RootPath);
            session.ClearPendingCommands();
            session.IsFileSelectionActive = false;

            _ = await TrackMessageAsync(outputService.RemoveReplyKeyboardAsync(userId, queuedMessage), session);
        }
        finally
        {
            await typingCts.CancelAsync();
            try { await typingTask; } catch (OperationCanceledException) { }
        }
    }

    private async Task TypingLoopAsync(long userId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await outputService.SendChatActionAsync(userId, cancellationToken);
                await Task.Delay(4000, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Typing indicator failed for user {UserId}", userId);
                return;
            }
        }
    }

    private async Task<bool> CheckDailyFileLimitAsync(long userId, string username, UserSession session, int newFileCount)
    {
        if (_rateLimitOptions.MaxFilesPerUserPerDay <= 0)
        {
            return true;
        }

        var sinceUtc = DateTime.UtcNow.AddDays(-1);
        var queuedToday = await dataServices.Sessions.CountQueuedFilesByUserSinceAsync(userId, sinceUtc);
        var remaining = _rateLimitOptions.MaxFilesPerUserPerDay - queuedToday;

        if (newFileCount <= remaining)
        {
            return true;
        }

        logger.LogWarning(
            "Job submit blocked: user={Username} ({UserId}), reason=daily_file_limit, queued={Queued}, requested={Requested}, limit={Limit}",
            username, userId, queuedToday, newFileCount, _rateLimitOptions.MaxFilesPerUserPerDay);

        var message = remaining > 0
            ? $"⚠️ Дневной лимит файлов: {_rateLimitOptions.MaxFilesPerUserPerDay}. Уже в очереди за 24 часа: {queuedToday}. Можно добавить ещё {remaining}."
            : $"⚠️ Дневной лимит файлов: {_rateLimitOptions.MaxFilesPerUserPerDay}. За последние 24 часа лимит уже исчерпан.";

        await SendWarningAndCleanupAsync(userId, session, message);
        return false;
    }

    private Task SendFileActionsReplyKeyboardAsync(long userId, UserSession session)
    {
        return HandlerHelpers.SendActionsReplyKeyboardAsync(outputService, dataServices.MessageTracking, userId, session,
                _options.IsAtProjectLevel(session.CurrentPath)
                    ? keyboardBuilder.GetProjectActionsReplyKeyboardAsync
                    : keyboardBuilder.GetSectionActionsReplyKeyboardAsync);
    }

    private async Task StartCommandSelectionAsync(long userId, UserSession session, CommandGroup commandGroup)
    {
        session.Reset(_options.RootPath);
        session.IsFileSelectionActive = false;

        var commandKeyboard = await keyboardBuilder.GetCommandKeyboardAsync(commandGroup, session);

        var replyKeyboard = await keyboardBuilder.GetCommandActionsReplyKeyboardAsync();

        var commandSelectionMessage = await TrackMessageAsync(outputService.SendMessageWithKeyboardAsync(userId, "Выберите команду:", commandKeyboard), session);
        session.CommandSelectionMessageId = commandSelectionMessage?.Id;
        var actionsMessage = await TrackMessageAsync(outputService.SendMessageWithReplyKeyboardAsync(userId, "Подтвердите выбор:", replyKeyboard), session);
        session.LastActionsMessageId = actionsMessage?.Id;
    }

    private async Task SendRegistrationMessageAsync(long userId, UserSession session)
    {
        var keyboard = new InlineKeyboardMarkup(
        [
            [
                InlineKeyboardButton.WithCallbackData("Запросить доступ", CallbackPrefixes.RequestAccess)
            ]
        ]);
        _=await TrackMessageAsync(outputService.SendMessageWithKeyboardAsync(userId,
            "Добро пожаловать!\n\nУ вас нет доступа к этому боту. Нажмите кнопку ниже, чтобы запросить доступ.",
            keyboard), session);
    }

    private async Task SendHelpMessageAsync(long userId, UserSession session)
    {
        var helpText = new StringBuilder()
            .AppendLine("Доступные команды:\n")
            .AppendLine("/export — экспорт файлов в PDF, DWG, NWC, IFC")
            .AppendLine("/automation — автоматизация задач связанными с BIM")
            .AppendLine("/status — статус выполнения задач и управление сессиями")
            .AppendLine("/help — справка по командам")
            .ToString();

        _=await TrackMessageAsync(outputService.SendMessageAsync(userId, helpText), session);
    }

    private async Task<Message?> TrackMessageAsync(Task<Message?> task, UserSession session)
    {
#pragma warning disable VSTHRD003 // Foreign Task passed as parameter — intentionally awaited here
        var msg = await task;
#pragma warning restore VSTHRD003
        if (msg != null)
        {
            var sessionId = session.SessionId > 0 ? session.SessionId : (int?)null;
            await dataServices.MessageTracking.TrackMessageAsync(msg.Chat.Id, msg.MessageId, sessionId);
        }
        return msg;
    }

    private async Task SendWarningAndCleanupAsync(long userId, UserSession session, string message)
    {
        Message? warning;

        if (session.IsFileSelectionActive)
        {
            var replyKeyboard = _options.IsAtProjectLevel(session.CurrentPath)
                ? await keyboardBuilder.GetProjectActionsReplyKeyboardAsync()
                : await keyboardBuilder.GetSectionActionsReplyKeyboardAsync();
            warning = await TrackMessageAsync(outputService.SendMessageWithReplyKeyboardAsync(userId, message, replyKeyboard), session);
        }
        else if (session.CommandSelectionMessageId.HasValue || session.PendingCommand.Count > 0)
        {
            var replyKeyboard = await keyboardBuilder.GetCommandActionsReplyKeyboardAsync();
            warning = await TrackMessageAsync(outputService.SendMessageWithReplyKeyboardAsync(userId, message, replyKeyboard), session);
        }
        else
        {
            warning = await TrackMessageAsync(outputService.SendMessageAsync(userId, message), session);
        }

        await CleanupCurrentViewAsync(userId, session, warning?.Id);
    }

    private async Task CleanupCurrentViewAsync(long userId, UserSession session, params int?[] extraKeepMessageIds)
    {
        var keepMessageIds = new[]
            {
                session.CommandSelectionMessageId,
                session.FileSelectionMessageId,
                session.StatusMessageId,
                session.LastActionsMessageId
            }
            .Concat(extraKeepMessageIds)
            .Where(messageId => messageId.HasValue)
            .Select(messageId => messageId!.Value);

        await outputService.ClearChatHistoryAsync(userId, session, keepMessageIds);
    }

    private static string NormalizeCommandText(string text)
    {
        if (!text.StartsWith('/'))
        {
            return text;
        }

        var mentionIndex = text.IndexOf('@');
        if (mentionIndex > 0)
        {
            text = text[..mentionIndex];
        }

        return text.ToLowerInvariant();
    }

    private static string BuildJobQueuedMessage(
        IReadOnlyList<string> commandNames,
        string projectName,
        IEnumerable<string> sectionNames,
        int fileCount)
    {
        var builder = new StringBuilder()
            .AppendLine("✅ *Задание успешно добавлено в очередь*")
            .AppendLine()
            .AppendLine("🧰 *Команды*");

        foreach (var commandName in commandNames)
        {
            _=builder.AppendLine($"• {MarkdownHelper.Escape(commandName)}");
        }

        _=builder
            .AppendLine()
            .AppendLine("📌 *Проект*")
            .AppendLine($"`{MarkdownHelper.Escape(projectName)}`")
            .AppendLine()
            .AppendLine("📂 *Разделы*");

        foreach (var sectionName in sectionNames)
        {
            _=builder.AppendLine($"• {MarkdownHelper.Escape(sectionName)}");
        }

        _=builder
            .AppendLine()
            .AppendLine($"📄 *Количество файлов:* `{fileCount}`");

        return builder.ToString();
    }

    private static string GetCurrentProjectName(UserSession session)
    {
        var projectDirectory = Directory.GetParent(session.CurrentPath);
        return projectDirectory == null
            ? GetSafePathName(session.CurrentPath)
            : GetSafePathName(projectDirectory.FullName);
    }

    private static string GetSafePathName(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private async Task<List<string>> CollectRvtFilesAsync(IReadOnlySet<string> sectionPaths, CancellationToken cancellationToken)
    {
        var rvtDirs = sectionPaths
            .Select(_options.GetRvtPath)
            .Where(Directory.Exists)
            .ToArray();

        if (rvtDirs.Length == 0)
            return [];

        var scanTasks = rvtDirs
            .Select(dir => Task.Run(() => EnumerateValidRvtFiles(dir), cancellationToken));

        var results = await Task.WhenAll(scanTasks);
        var allFiles = results.SelectMany(f => f).ToList();
        return RevitFileDeduplicator.Deduplicate(allFiles);
    }

    private static IEnumerable<string> EnumerateValidRvtFiles(string rvtDir)
    {
        try
        {
            return new DirectoryInfo(rvtDir)
                .EnumerateFiles("*.rvt", _rvtEnumOptions)
                .Where(IsValidRevitFile)
                .Select(fi => fi.FullName)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsValidRevitFile(FileInfo fi)
    {
        var name = Path.GetFileNameWithoutExtension(fi.Name);

        if (name.Length is <10 or >50)
        {
            return false;
        }

        if (name.EndsWith("отсоединено", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!RvtSectionPattern().IsMatch(name))
        {
            return false;
        }

        try
        {
            return fi.Length > _rvtMinFileSizeBytes;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private enum CommandStrategy
    {
        Start,
        AccessDenied,
        CommandSelectionAction,
        SlashCommand,
    }

    private enum CommandExecutionStatus
    {
        Handled,
        AccessDenied
    }

    private sealed record UserCommandContext(
        MessageDto Message,
        UserSession Session,
        long UserId,
        long ChatId,
        string RawText,
        string Command,
        string Username,
        BotUser? User,
        bool HasAccess);

    private sealed record CommandExecutionResult(CommandExecutionStatus Status)
    {
        public static CommandExecutionResult Handled()
        {
            return new CommandExecutionResult(CommandExecutionStatus.Handled);
        }

        public static CommandExecutionResult AccessDenied()
        {
            return new CommandExecutionResult(CommandExecutionStatus.AccessDenied);
        }
    }
}
