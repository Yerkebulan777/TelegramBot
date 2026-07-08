using Microsoft.Extensions.Options;
using System.Collections.Frozen;
using System.Text;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.DTOs;
using TelegramBot.Core.Models;
using TelegramBot.Data;
using TelegramBot.Server.Helpers;
using TelegramBot.Server.Middleware;
using TelegramBot.Server.Models;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Services.Application;

public sealed partial class SlashCommandService(
    SessionDataService sessionDataService,
    MessageTrackingService messageTrackingService,
    TelegramOutputService outputService,
    KeyboardBuilder keyboardBuilder,
    AuthorizationMiddleware accessValidator,
    SessionsListRenderer sessionsListRenderer,
    IOptions<FileSystemOptions> fileSystemOptions,
    IOptions<RateLimitOptions> rateLimitOptions,
    ILogger<SlashCommandService> logger)
{
    private static readonly FrozenDictionary<string, int> _commandPriorityMap =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["PDF"] = CommandPriorities.Critical,
            ["DWG"] = CommandPriorities.Critical,
            ["NWC"] = CommandPriorities.High,
            ["IFC"] = CommandPriorities.Medium,
            ["DATA"] = CommandPriorities.Low,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private readonly FileSystemOptions _options = fileSystemOptions.Value;
    private readonly RateLimitOptions _rateLimitOptions = rateLimitOptions.Value;

    public async Task HandleUserCommandAsync(MessageDto message, UserSession session, CancellationToken cancellationToken = default)
    {
        var userId = message.UserId;
        var username = message.Username;
        var rawText = message.Text!;
        var command = NormalizeCommandText(rawText);

        ArgumentNullException.ThrowIfNullOrWhiteSpace(username);
        logger.LogDebug("Cmd: cmd={Command}, user={Username} ({UserId})", command, username, userId);

        var access = await accessValidator.ValidateAsync(userId);
        if (command == "/start" && !access.IsActive)
        {
            _ = await accessValidator.RefreshApprovedAdminUserAsync(userId, username, access.User);
            access = await accessValidator.ValidateAsync(userId);
        }

        if (command != "/start" && !access.IsActive)
        {
            logger.LogWarning("Cmd rejected: cmd={Command}, user={Username} ({UserId}), reason=access",
                command, username, userId);
            var chatId = message.ChatId == 0 ? userId : message.ChatId;
            await SendSafeResponseAsync(chatId, "У вас нет доступа. Введите /start для запроса доступа.", session);
            return;
        }

        if (command.StartsWith('/'))
        {
            await outputService.ClearChatHistoryAsync(userId, session);
        }

        if (command == "/start")
        {
            session.Reset(_options.RootPath);
            if (access.IsActive)
            {
                await SendHelpMessageAsync(userId, session);
            }
            else
            {
                await SendRegistrationMessageAsync(userId, session);
            }
            return;
        }

        if (IsCommandSelectionAction(rawText) &&
            await HandleCommandSelectionActionsAsync(userId, username, rawText, session, cancellationToken))
        {
            return;
        }

        await HandleSlashCommandAsync(command, message, session, username);
    }

    private async Task HandleSlashCommandAsync(string command, MessageDto message, UserSession session, string username)
    {
        var userId = message.UserId;
        var isSlashCommand = command.StartsWith('/');

        switch (command)
        {
            case "/export":
                logger.LogDebug("/export: user={Username} ({UserId})", username, userId);
                await StartCommandSelectionAsync(userId, session, CommandGroup.Export);
                break;

            case "/status":
                logger.LogDebug("/status: user={Username} ({UserId})", username, userId);
                session.Reset(_options.RootPath);
                session.StatusFilter = StatusFilters.All;

                var sent = await sessionsListRenderer.SendNewAsync(userId, session.StatusFilter);
                var tracked = await messageTrackingService.TrackAsync(sent, session);
                session.StatusMessageId = tracked?.Id;
                break;

            case "/automation":
                logger.LogDebug("/automation: user={Username} ({UserId})", username, userId);
                await StartCommandSelectionAsync(userId, session, CommandGroup.Automation);
                break;

            case "/help":
                logger.LogDebug("/help: user={Username} ({UserId})", username, userId);
                session.Reset(_options.RootPath);
                await SendHelpMessageAsync(userId, session);
                break;

            default:
                if (isSlashCommand)
                {
                    logger.LogWarning("Unknown cmd '{Command}' from {Username} ({UserId})", command, username, userId);
                }
                else
                {
                    logger.LogDebug("Ignoring non-cmd text from {Username} ({UserId})", username, userId);
                }

                break;
        }
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
                logger.LogDebug("Stale Confirm from {Username} ({UserId}), cleaning", username, userId);
                if (session.LastUserMessageId.HasValue)
                {
                    try
                    {
                        await outputService.DeleteMessageAsync(userId, session.LastUserMessageId.Value);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Delete stale Confirm fail: user={UserId}", userId);
                    }
                    session.LastUserMessageId = null;
                }
                return true;

            case ButtonTexts.Cancel:
                logger.LogDebug("{Username} ({UserId}) cancelled selection", username, userId);

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
            logger.LogDebug("{Username} ({UserId}) apply with no cmds", username, userId);
            await SendWarningAndCleanupAsync(userId, session, "Сначала выберите хотя бы одну команду.");
            return;
        }

        logger.LogDebug("Selection confirmed: user={Username} ({UserId}), count={Count}",
            username, userId, session.PendingCommand.Count);

        await outputService.ClearChatHistoryAsync(userId, session);

        session.CommandSelectionMessageId = null;
        session.CurrentPath = _options.RootPath;
        session.IsFileSelectionActive = true;

        var keyboard = keyboardBuilder.GetSelectionKeyboard(userId, session);
        var selectionMessage = await messageTrackingService.TrackAsync(outputService.SendMessageWithKeyboardAsync(userId, "Выберите папки:", keyboard), session);
        session.FileSelectionMessageId = selectionMessage?.Id;
        await SendFileActionsReplyKeyboardAsync(userId, session);
    }

    private async Task ConfirmFileSelectionAsync(long userId, string username, UserSession session, CancellationToken cancellationToken)
    {
        if (!session.FileSelectionMessageId.HasValue)
        {
            logger.LogWarning("Job blocked: {Username} ({UserId}), reason=no_file_selection_msg", username, userId);
            await RemoveReplyKeyboardAsync(userId, session, username);
            session.IsFileSelectionActive = false;
            await RejectAndWarnAsync(userId, session, "Сообщение выбора файлов не найдено.");
            return;
        }

        if (_options.IsAtProjectLevel(session.CurrentPath))
        {
            var selectedProject = session.GetSelectedFiles().FirstOrDefault();
            if (selectedProject == null)
            {
                logger.LogDebug("Project confirm blocked: {Username} ({UserId}), reason=no_project", username, userId);
                await SendWarningAndCleanupAsync(userId, session, "⚠️ Сначала выберите проект.");
                return;
            }

            session.CurrentPath = Path.Combine(selectedProject, _options.ProjectDirectoryName);

            session.ClearSelectedFiles();

            logger.LogDebug("{Username} ({UserId}) confirmed project '{Project}', nav to 01_PROJECT", username, userId, Path.GetFileName(selectedProject));

            var keyboard = keyboardBuilder.GetSelectionKeyboard(userId, session);
            await outputService.EditMessageReplyMarkupAsync(userId, session.FileSelectionMessageId.Value, keyboard);
            await SendFileActionsReplyKeyboardAsync(userId, session);
            await CleanupCurrentViewAsync(userId, session);
            return;
        }

        // Remove reply keyboard immediately for final confirmation so it doesn't linger in any case.
        await RemoveReplyKeyboardAsync(userId, session, username);
        session.IsFileSelectionActive = false;

        var selectedFiles = session.GetSelectedFiles();
        if (selectedFiles.Count == 0)
        {
            logger.LogDebug("Job blocked: {Username} ({UserId}), reason=no_files", username, userId);
            await RejectAndWarnAsync(userId, session, "⚠️ Сначала выберите хотя бы один файл.");
            return;
        }

        logger.LogDebug(
            "Job submit: user={Username} ({UserId}), cmds={CommandCount}, files={FileCount}",
            username, userId, session.PendingCommand.Count, selectedFiles.Count);

        var commandNames = session.PendingCommandName;
        var projectName = GetProjectName(selectedFiles.First());
        var sectionNames = selectedFiles
            .Select(GetSectionFolderName)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        using var typingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var typingTask = TypingLoopAsync(userId, typingCts.Token);

        try
        {
            var filesToProcess = selectedFiles.Where(File.Exists).ToList();
            if (filesToProcess.Count == 0)
            {
                logger.LogWarning("Job blocked: {Username} ({UserId}), reason=no_files_found", username, userId);
                await RejectAndWarnAsync(userId, session, $"⚠️ Выбранные файлы проекта «{projectName}» не найдены на диске.");
                return;
            }

            if (!await CheckDailyFileLimitAsync(userId, username, session, filesToProcess.Count))
            {
                return;
            }

            var priorities = session.PendingCommand
                .Select(c => _commandPriorityMap.TryGetValue(c, out var p) ? p : CommandPriorities.Default);

            var correlationId = Guid.NewGuid().ToString("N");
            var (sessionId, queuedFileCount, skippedPairs) = await sessionDataService.CreateSessionWithCommandsAsync(
                session.PendingCommand, filesToProcess, userId, username, filesToProcess.Count, projectName, priorities, correlationId);
            if (sessionId is null)
            {
                // Каждая пара (команда, файл) пойман уникальным индексом idx_commands_active_unique — все пары дубли
                logger.LogWarning("Job blocked: {Username} ({UserId}), reason=all_dup_cmds", username, userId);
                await RejectAndWarnAsync(userId, session, $"⚠️ Выбранные файлы проекта «{projectName}» уже находятся в очереди выполнения.");
                return;
            }

            var queuedMessage = BuildJobQueuedMessage(commandNames, projectName, sectionNames, queuedFileCount, skippedPairs);

            logger.LogInformation(
                "Job queued: session={SessionId}, corr={CorrelationId}, user={Username} ({UserId}), cmds={CommandCount}, files={FileCount}, skipped={SkippedCount}",
                sessionId, correlationId, username, userId, session.PendingCommand.Count, queuedFileCount, skippedPairs.Count);

            session.SessionId = checked((int)sessionId.Value);
            await outputService.ClearChatHistoryAsync(userId, session);

            session.ResetNavigation(_options.RootPath);
            session.ClearPendingCommands();
            session.IsFileSelectionActive = false;

            _ = await messageTrackingService.TrackAsync(outputService.RemoveReplyKeyboardAsync(userId, queuedMessage), session);
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
                logger.LogDebug(ex, "Typing indicator fail: user={UserId}", userId);
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
        var queuedToday = await sessionDataService.CountQueuedFilesByUserSinceAsync(userId, sinceUtc);
        var remaining = _rateLimitOptions.MaxFilesPerUserPerDay - queuedToday;

        if (newFileCount <= remaining)
        {
            return true;
        }

        logger.LogWarning(
            "Job blocked: {Username} ({UserId}), reason=daily_limit, queued={Queued}, requested={Requested}, limit={Limit}",
            username, userId, queuedToday, newFileCount, _rateLimitOptions.MaxFilesPerUserPerDay);

        var message = remaining > 0
            ? $"⚠️ Дневной лимит файлов: {_rateLimitOptions.MaxFilesPerUserPerDay}. Уже в очереди за 24 часа: {queuedToday}. Можно добавить ещё {remaining}."
            : $"⚠️ Дневной лимит файлов: {_rateLimitOptions.MaxFilesPerUserPerDay}. За последние 24 часа лимит уже исчерпан.";

        await RejectAndWarnAsync(userId, session, message);
        return false;
    }

    private async Task SendFileActionsReplyKeyboardAsync(long userId, UserSession session)
    {
        if (session.LastActionsMessageId.HasValue)
        {
            await outputService.DeleteMessageAsync(userId, session.LastActionsMessageId.Value);
            session.LastActionsMessageId = null;
        }

        var replyKeyboard = keyboardBuilder.GetFileActionsReplyKeyboard();
        var message = await messageTrackingService.TrackAsync(
            outputService.SendMessageWithReplyKeyboardAsync(userId, "Действия:", replyKeyboard),
            session);
        session.LastActionsMessageId = message?.Id;
    }

    private async Task StartCommandSelectionAsync(long userId, UserSession session, CommandGroup commandGroup)
    {
        session.Reset(_options.RootPath);
        session.IsFileSelectionActive = false;

        var commandKeyboard = keyboardBuilder.GetCommandKeyboard(commandGroup, session);

        var replyKeyboard = keyboardBuilder.GetCommandActionsReplyKeyboard();

        var commandSelectionMessage = await messageTrackingService.TrackAsync(outputService.SendMessageWithKeyboardAsync(userId, "Выберите команду:", commandKeyboard), session);
        session.CommandSelectionMessageId = commandSelectionMessage?.Id;
        var actionsMessage = await messageTrackingService.TrackAsync(outputService.SendMessageWithReplyKeyboardAsync(userId, "Подтвердите выбор:", replyKeyboard), session);
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
        _=await messageTrackingService.TrackAsync(outputService.SendMessageWithKeyboardAsync(userId,
            "Добро пожаловать!\n\nУ вас нет доступа к этому боту. Нажмите кнопку ниже, чтобы запросить доступ.",
            keyboard), session);
    }

    private async Task SendSafeResponseAsync(long chatId, string message, UserSession session)
    {
        try
        {
            _ = await messageTrackingService.TrackAsync(outputService.SendMessageAsync(chatId, message), session);
        }
        catch (ApiRequestException ex)
        {
            logger.LogWarning(ex, "Send cmd response fail: chat={ChatId}", chatId);
        }
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

        _=await messageTrackingService.TrackAsync(outputService.SendMessageAsync(userId, helpText), session);
    }

    /// <summary>
    /// Сбрасывает pending-команды (поскольку дальнейшее выполнение не предполагается) и отправляет warning.
    /// </summary>
    private async Task RejectAndWarnAsync(long userId, UserSession session, string message)
    {
        session.ClearPendingCommands();
        await SendWarningAndCleanupAsync(userId, session, message);
    }

    private async Task SendWarningAndCleanupAsync(long userId, UserSession session, string message)
    {
        var warning = session.IsFileSelectionActive
            ? await SendWarningWithReplyKeyboardAsync(userId, session, message, keyboardBuilder.GetFileActionsReplyKeyboard())
            : session.CommandSelectionMessageId.HasValue || session.PendingCommand.Count > 0
                ? await SendWarningWithReplyKeyboardAsync(userId, session, message, keyboardBuilder.GetCommandActionsReplyKeyboard())
                : await messageTrackingService.TrackAsync(outputService.SendMessageAsync(userId, message), session);
        await CleanupCurrentViewAsync(userId, session, warning?.Id);
    }

    private async Task<Message?> SendWarningWithReplyKeyboardAsync(
        long userId,
        UserSession session,
        string message,
        ReplyKeyboardMarkup replyKeyboard)
    {
        return await messageTrackingService.TrackAsync(
            outputService.SendMessageWithReplyKeyboardAsync(userId, message, replyKeyboard),
            session);
    }

    private async Task CleanupCurrentViewAsync(long userId, UserSession session, int? extraKeepMessageId = null)
    {
        var keepMessageIds = new[]
            {
                session.CommandSelectionMessageId,
                session.FileSelectionMessageId,
                session.StatusMessageId,
                session.LastActionsMessageId,
                extraKeepMessageId
            }
            .Where(messageId => messageId.HasValue)
            .Select(messageId => messageId!.Value);

        await outputService.ClearChatHistoryAsync(userId, session, keepMessageIds);
    }

    private async Task RemoveReplyKeyboardAsync(long userId, UserSession session, string username)
    {
        try
        {
            _ = await messageTrackingService.TrackAsync(outputService.RemoveReplyKeyboardAsync(userId, "Ожидайте обработку задания 🤔"), session);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Remove reply keyboard fail: user={Username} ({UserId})", username, userId);
        }
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
        int fileCount,
        IReadOnlyList<(string Command, string FilePath)>? skippedPairs = null)
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

        if (skippedPairs is { Count: > 0 })
        {
            _=builder
                .AppendLine()
                .AppendLine($"⚠️ *Уже в очереди, пропущено:* `{skippedPairs.Count}`");

            foreach (var (command, filePath) in skippedPairs)
            {
                _=builder.AppendLine($"• {MarkdownHelper.Escape(Path.GetFileName(filePath))} — {MarkdownHelper.Escape(command)}");
            }
        }

        return builder.ToString();
    }

    /// <summary>Имя папки проекта (родитель 01_PROJECT) для выбранного файла.</summary>
    private string GetProjectName(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        while (!string.IsNullOrEmpty(dir))
        {
            if (string.Equals(Path.GetFileName(dir), _options.ProjectDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                var parent = Path.GetDirectoryName(dir);
                return parent != null ? GetSafePathName(parent) : GetSafePathName(dir);
            }

            dir = Path.GetDirectoryName(dir);
        }

        return GetSafePathName(filePath);
    }

    private static string GetSafePathName(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    /// <summary>Имя папки раздела (родитель которой — 01_PROJECT) для выбранного файла, либо null.</summary>
    private string? GetSectionFolderName(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        while (!string.IsNullOrEmpty(dir))
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent != null && string.Equals(Path.GetFileName(parent), _options.ProjectDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                return GetSafePathName(dir);
            }

            dir = parent;
        }

        return null;
    }
}
