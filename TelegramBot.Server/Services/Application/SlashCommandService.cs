using Microsoft.Extensions.Options;
using System.Text;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Helpers;
using TelegramBot.Core.Models;
using TelegramBot.Data;
using TelegramBot.Server.Helpers;
using TelegramBot.Server.Models;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Services.Application;

public sealed class SlashCommandService(
    SessionDataService sessionDataService,
    MessageTrackingService messageTrackingService,
    TelegramOutputService outputService,
    KeyboardBuilder keyboardBuilder,
    SessionsListRenderer sessionsListRenderer,
    IOptions<FileSystemOptions> fileSystemOptions,
    IOptions<RateLimitOptions> rateLimitOptions,
    RootPathProvider rootPathProvider,
    UncRootPathValidator uncRootPathValidator,
    ILogger<SlashCommandService> logger)
{
    private readonly FileSystemOptions _fileSystemOptions = fileSystemOptions.Value;
    private readonly RateLimitOptions _rateLimitOptions = rateLimitOptions.Value;

    public async Task HandleUserCommandAsync(Message message, UserSession session, CancellationToken cancellationToken = default)
    {
        var sender = message.From
            ?? throw new InvalidOperationException("Message.From is null.");
        var userId = sender.Id;
        var username = sender.Username ?? sender.FirstName;
        var rawText = message.Text!;
        var command = NormalizeCommandText(rawText);

        if (session.AwaitingRootPath && !command.StartsWith('/'))
        {
            logger.LogDebug("Root path submitted: user={UserId}", userId);
            await TrySetRootPathAsync(message, session, cancellationToken);
            return;
        }

        logger.LogDebug("Cmd: cmd={Command}, user={Username} ({UserId})", command, username, userId);

        if (command.StartsWith('/'))
        {
            await outputService.ClearChatHistoryAsync(userId, session, cancellationToken);
        }

        if (command == "/start")
        {
            session.Reset(await rootPathProvider.GetRootPathAsync(cancellationToken));
            await SendHelpMessageAsync(userId, session, cancellationToken);
            return;
        }

        await HandleSlashCommandAsync(command, userId, session, username!, cancellationToken);
    }

    private async Task HandleSlashCommandAsync(
        string command, long userId, UserSession session, string username, CancellationToken cancellationToken)
    {
        var isSlashCommand = command.StartsWith('/');

        switch (command)
        {
            case "/export":
                logger.LogDebug("/export: user={Username} ({UserId})", username, userId);
                await StartCommandSelectionAsync(userId, session, CommandGroup.Export, cancellationToken);
                break;

            case "/status":
                logger.LogDebug("/status: user={Username} ({UserId})", username, userId);
                session.Reset(await rootPathProvider.GetRootPathAsync(cancellationToken));
                session.StatusFilter = StatusFilters.All;

                var sent = await sessionsListRenderer.SendNewAsync(userId, session.StatusFilter, cancellationToken: cancellationToken);
                var tracked = await messageTrackingService.TrackAsync(sent, session);
                session.StatusMessageId = tracked?.Id;
                break;

            case "/automation":
                logger.LogDebug("/automation: user={Username} ({UserId})", username, userId);
                await StartCommandSelectionAsync(userId, session, CommandGroup.Automation, cancellationToken);
                break;

            case "/help":
                logger.LogDebug("/help: user={Username} ({UserId})", username, userId);
                session.Reset(await rootPathProvider.GetRootPathAsync(cancellationToken));
                await SendHelpMessageAsync(userId, session, cancellationToken);
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

    internal async Task ConfirmFileSelectionAsync(long userId, string username, UserSession session, CancellationToken cancellationToken)
    {
        var flow = session.Selection;

        if (!session.FileSelectionMessageId.HasValue)
        {
            logger.LogWarning("Job blocked: {Username} ({UserId}), reason=no_file_selection_msg", username, userId);
            await RejectAndWarnAsync(userId, session, "Сообщение выбора файлов не найдено.", cancellationToken);
            return;
        }

        var outcome = flow.Confirm();

        switch (outcome.Kind)
        {
            case SelectionFlow.ConfirmOutcomeKind.BlockedNoProject:
                logger.LogDebug("Project confirm blocked: {Username} ({UserId}), reason=no_project", username, userId);
                await SendWarningAndCleanupAsync(userId, session, "⚠️ Сначала выберите проект.", cancellationToken);
                return;

            case SelectionFlow.ConfirmOutcomeKind.Advanced:
                logger.LogDebug("{Username} ({UserId}) confirmed project '{Project}', nav to 01_PROJECT",
                    username, userId, Path.GetFileName(Path.GetDirectoryName(flow.CurrentPath)));
                await RenderSelectionAsync(userId, session);
                await CleanupCurrentViewAsync(userId, session, cancellationToken);
                return;
        }

        if (outcome.Kind == SelectionFlow.ConfirmOutcomeKind.BlockedNoFiles)
        {
            logger.LogDebug("Job blocked: {Username} ({UserId}), reason=no_files", username, userId);
            await RejectAndWarnAsync(userId, session, "⚠️ Сначала выберите хотя бы один файл.", cancellationToken);
            return;
        }

        var submission = outcome.Submission
            ?? throw new InvalidOperationException("ReadyToSubmit without submission");
        var selectedFiles = submission.Files;

        logger.LogDebug(
            "Job submit: user={Username} ({UserId}), cmds={CommandCount}, files={FileCount}",
            username, userId, submission.Commands.Count, selectedFiles.Count);

        var commandNames = submission.Commands.Select(GetCommandDisplayName).ToArray();
        var projectName = GetProjectName(selectedFiles.First(), _fileSystemOptions.ProjectDirectoryName);
        var sectionNames = selectedFiles
            .Select(file => GetSectionFolderName(file, _fileSystemOptions.ProjectDirectoryName))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        using var typingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var typingTask = TypingLoopAsync(userId, typingCts.Token);

        try
        {
            var filesToProcess = await FileExistence.ExistingPathsAsync(
                selectedFiles, TimeSpan.FromSeconds(3), cancellationToken);
            if (filesToProcess.Count == 0)
            {
                logger.LogWarning("Job blocked: {Username} ({UserId}), reason=no_files_found", username, userId);
                await RejectAndWarnAsync(userId, session, $"⚠️ Выбранные файлы проекта «{projectName}» не найдены на диске.", cancellationToken);
                return;
            }

            var priorities = submission.Commands.Select(CommandTraits.GetPriority);

            var correlationId = Guid.NewGuid().ToString("N");
            var created = await sessionDataService.CreateSessionWithCommandsAsync(
                submission.Commands, filesToProcess, userId, username, filesToProcess.Count, session.RootPath, projectName, priorities, correlationId,
                maxFilesPerUserPerDay: _rateLimitOptions.MaxFilesPerUserPerDay);
            if (created.Status == SessionCreateStatus.DailyLimitExceeded)
            {
                logger.LogWarning(
                    "Job blocked: {Username} ({UserId}), reason=daily_limit, queued={Queued}, requested={Requested}, limit={Limit}",
                    username, userId, created.DailyLimitQueuedToday, filesToProcess.Count, _rateLimitOptions.MaxFilesPerUserPerDay);
                await RejectAndWarnAsync(
                    userId, session, FormatDailyLimitMessage(created.DailyLimitQueuedToday), cancellationToken);
                return;
            }
            if (created.Status != SessionCreateStatus.Created || created.SessionId is null)
            {
                // Каждая пара (команда, файл) пойман уникальным индексом idx_commands_active_unique — все пары дубли
                logger.LogWarning("Job blocked: {Username} ({UserId}), reason=all_dup_cmds", username, userId);
                await CancelSelectionAsync(userId, session, cancellationToken, JobMessageFormatter.BuildConflictsMessage(created.SkippedPairs));
                return;
            }

            var sessionId = created.SessionId.Value;
            var queuedFileCount = created.QueuedFileCount;
            var skippedPairs = created.SkippedPairs;

            var skipped = skippedPairs.Select(p => (p.Command, p.FilePath)).ToHashSet();
            var queuedFiles = filesToProcess
                .Where(file => submission.Commands.Any(command => !skipped.Contains((command, file))))
                .ToArray();
            var queuedMessage = JobMessageFormatter.BuildJobQueuedMessage(commandNames, projectName, sectionNames, queuedFiles, skippedPairs);

            logger.LogInformation(
                "Job queued: session={SessionId}, corr={CorrelationId}, user={Username} ({UserId}), cmds={CommandCount}, files={FileCount}, skipped={SkippedCount}",
                sessionId, correlationId, username, userId, submission.Commands.Count, queuedFileCount, skippedPairs.Count);

            session.SessionId = sessionId;
            await outputService.ClearChatHistoryAsync(userId, session, cancellationToken);

            session.Selection.Reset(session.RootPath);

            _ = await messageTrackingService.TrackAsync(
                outputService.SendMessageAsync(userId, queuedMessage, cancellationToken), session,
                TrackedMessageKinds.JobStatus);
        }
        finally
        {
            await typingCts.CancelAsync();
            try { await typingTask; } catch (OperationCanceledException) { }
        }
    }

    /// <summary>Перерисовывает selection-клавиатуру из текущего состояния сессии.</summary>
    private async Task RenderSelectionAsync(long userId, UserSession session)
    {
        var keyboard = keyboardBuilder.GetSelectionKeyboard(session);
        await outputService.EditMessageReplyMarkupAsync(userId, session.FileSelectionMessageId!.Value, keyboard);
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

    private string FormatDailyLimitMessage(int queuedToday)
    {
        var remaining = _rateLimitOptions.MaxFilesPerUserPerDay - queuedToday;
        return remaining > 0
            ? $"⚠️ Дневной лимит файлов: {_rateLimitOptions.MaxFilesPerUserPerDay}. Уже в очереди за 24 часа: {queuedToday}. Можно добавить ещё {remaining}."
            : $"⚠️ Дневной лимит файлов: {_rateLimitOptions.MaxFilesPerUserPerDay}. За последние 24 часа лимит уже исчерпан.";
    }

    private async Task StartCommandSelectionAsync(
        long userId, UserSession session, CommandGroup commandGroup, CancellationToken cancellationToken)
    {
        var rootPath = await rootPathProvider.GetRootPathAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            await SendSafeResponseAsync(userId, "⚠️ Корневой сетевой путь не настроен. Откройте /help и задайте его.", session, cancellationToken);
            return;
        }

        session.Reset(rootPath);

        var commandKeyboard = keyboardBuilder.GetCommandKeyboard(commandGroup, session);

        var commandSelectionMessage = await messageTrackingService.TrackAsync(
            outputService.SendMessageWithKeyboardAsync(userId, "Выберите команду:", commandKeyboard, cancellationToken), session);
        session.CommandSelectionMessageId = commandSelectionMessage?.Id;
    }

    private async Task SendSafeResponseAsync(
        long chatId, string message, UserSession session, CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await messageTrackingService.TrackAsync(
                outputService.SendMessageAsync(chatId, message, cancellationToken), session,
                TrackedMessageKinds.Temporary);
        }
        catch (ApiRequestException ex)
        {
            logger.LogWarning(ex, "Send cmd response fail: chat={ChatId}", chatId);
        }
    }

    private async Task SendHelpMessageAsync(long userId, UserSession session, CancellationToken cancellationToken = default)
    {
        var configuredRootPath = await rootPathProvider.GetRootPathAsync(cancellationToken);
        var rootPath = string.IsNullOrWhiteSpace(configuredRootPath) ? "не настроен" : "настроен";
        var canConfigure = await rootPathProvider.CanConfigureRootPathAsync(userId, cancellationToken);
        var pendingChange = canConfigure ? await rootPathProvider.GetPendingRootPathChangeAsync(cancellationToken) : null;
        var activePendingChange = pendingChange is { IsPending: true }
            && pendingChange.CreatedAtUtc.AddMinutes(30) >= DateTimeOffset.UtcNow
            ? pendingChange
            : null;
        var helpText = new StringBuilder()
            .AppendLine("Доступные команды:\n")
            .AppendLine("/export — экспорт файлов в PDF, DWG, NWC, IFC")
            .AppendLine("/automation — автоматизация задач связанными с BIM")
            .AppendLine("/status — статус выполнения задач и управление сессиями")
            .AppendLine("/help — справка по командам")
            .AppendLine()
            .AppendLine($"Корневой путь: {rootPath}")
            .Append(activePendingChange is null
                ? string.Empty
                : "\nОжидает подтверждения заявка на смену рабочей папки.\n")
            .ToString();

        var response = canConfigure
            ? outputService.SendMessageWithKeyboardAsync(userId, helpText, keyboardBuilder.GetRootPathKeyboard(activePendingChange?.Id), cancellationToken)
            : outputService.SendMessageAsync(userId, helpText, cancellationToken);

        _ = await messageTrackingService.TrackAsync(response, session);
    }

    internal async Task<bool> BeginRootPathUpdateAsync(long userId, UserSession session, CancellationToken cancellationToken = default)
    {
        if (!await rootPathProvider.CanConfigureRootPathAsync(userId, cancellationToken))
        {
            logger.LogWarning("Rejected root path update attempt: user={UserId}", userId);
            return false;
        }

        await outputService.ClearChatHistoryAsync(userId, session, cancellationToken);
        session.AwaitingRootPath = true;
        _ = await messageTrackingService.TrackAsync(
            outputService.SendForceReplyAsync(userId, "Отправьте букву диска с проектами, например Z:\\ (можно и вложенную папку). Бот определит сетевой UNC-путь сам.", cancellationToken), session);
        return true;
    }

    internal async Task<string> DecidePendingRootPathChangeAsync(
        long userId,
        string requestId,
        bool apply,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(requestId, out var changeId))
        {
            return "⚠️ Некорректная заявка на смену рабочей папки.";
        }

        var pendingChange = await rootPathProvider.GetPendingRootPathChangeAsync(cancellationToken);
        if (pendingChange is null || pendingChange.Id != changeId || !pendingChange.IsPending
            || pendingChange.CreatedAtUtc.AddMinutes(30) < DateTimeOffset.UtcNow)
        {
            return "⚠️ Заявка уже обработана или истекла. Подготовьте новую в Windows.";
        }

        string? verifiedRootPath = null;
        if (apply && !uncRootPathValidator.TryValidate(pendingChange.UncPath, out verifiedRootPath, out _))
        {
            return "⚠️ Server не может проверить доступ к выбранной папке. Рабочий путь не изменён.";
        }

        var result = await rootPathProvider.DecidePendingRootPathChangeAsync(
            changeId, userId, verifiedRootPath, pendingChange.UncPath, apply, cancellationToken);
        return result switch
        {
            PendingRootPathChangeDecisionResult.Applied => "✅ Рабочая папка изменена. Новые задачи используют новый путь.",
            PendingRootPathChangeDecisionResult.Cancelled => "Изменение рабочей папки отменено.",
            PendingRootPathChangeDecisionResult.NotAdministrator => "⚠️ Рабочую папку может менять только администратор.",
            PendingRootPathChangeDecisionResult.NotAvailable => "⚠️ Заявка уже обработана или истекла.",
            PendingRootPathChangeDecisionResult.InvalidPath => "⚠️ Server не может проверить доступ к выбранной папке.",
            _ => "⚠️ Не удалось обработать заявку. Попробуйте ещё раз."
        };
    }

    private async Task TrySetRootPathAsync(Message message, UserSession session, CancellationToken cancellationToken)
    {
        var userId = message.From!.Id;
        if (!uncRootPathValidator.TryValidate(message.Text, out var rootPath, out var error))
        {
            await SendSafeResponseAsync(userId, $"⚠️ {error} Попробуйте ещё раз.", session, cancellationToken);
            return;
        }

        var updateResult = await rootPathProvider.SetRootPathAsync(rootPath, userId, cancellationToken);
        if (updateResult == RootPathUpdateResult.NotAdministrator)
        {
            session.AwaitingRootPath = false;
            await SendSafeResponseAsync(userId, "⚠️ Корневой путь может менять только администратор.", session, cancellationToken);
            return;
        }

        if (updateResult != RootPathUpdateResult.Updated)
        {
            await SendSafeResponseAsync(userId, "⚠️ Не удалось сохранить путь в базе данных. Попробуйте ещё раз.", session, cancellationToken);
            return;
        }

        session.Reset(rootPath);
        await outputService.ClearChatHistoryAsync(userId, session, cancellationToken);
        await SendHelpMessageAsync(userId, session, cancellationToken);
    }

    /// <summary>
    /// Полный сброс сессии (как по кнопке "Отмена"): чистит историю чата, сбрасывает состояние
    /// и отправляет либо переданное сообщение, либо стандартную справку.
    /// </summary>
    internal async Task CancelSelectionAsync(long userId, UserSession session, CancellationToken cancellationToken, string? message = null)
    {
        await outputService.ClearChatHistoryAsync(userId, session, cancellationToken);
        session.Reset(session.RootPath);

        if (message is null)
        {
            await SendHelpMessageAsync(userId, session, cancellationToken);
        }
        else
        {
            await SendSafeResponseAsync(userId, message, session, cancellationToken);
        }
    }

    /// <summary>
    /// Сбрасывает pending-команды (поскольку дальнейшее выполнение не предполагается) и отправляет warning.
    /// </summary>
    private async Task RejectAndWarnAsync(long userId, UserSession session, string message, CancellationToken cancellationToken)
    {
        session.Selection.ClearCommands();
        await SendWarningAndCleanupAsync(userId, session, message, cancellationToken);
    }

    private async Task SendWarningAndCleanupAsync(long userId, UserSession session, string message, CancellationToken cancellationToken)
    {
        var warning = await messageTrackingService.TrackAsync(
            outputService.SendMessageAsync(userId, message, cancellationToken), session,
            TrackedMessageKinds.Temporary);
        await CleanupCurrentViewAsync(userId, session, cancellationToken, warning?.Id);
    }

    private async Task CleanupCurrentViewAsync(long userId, UserSession session, CancellationToken cancellationToken, int? extraKeepMessageId = null)
    {
        var keepMessageIds = new[]
            {
                session.CommandSelectionMessageId,
                session.FileSelectionMessageId,
                session.StatusMessageId,
                extraKeepMessageId
            }
            .Where(messageId => messageId.HasValue)
            .Select(messageId => messageId!.Value);

        await outputService.ClearChatHistoryAsync(userId, session, cancellationToken, keepMessageIds);
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

    private static string GetProjectName(string filePath, string projectDirectoryName)
    {
        var dir = Path.GetDirectoryName(filePath);
        while (!string.IsNullOrEmpty(dir))
        {
            if (string.Equals(Path.GetFileName(dir), projectDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                var parent = Path.GetDirectoryName(dir);
                return parent != null ? GetSafePathName(parent) : GetSafePathName(dir);
            }

            dir = Path.GetDirectoryName(dir);
        }

        return GetSafePathName(filePath);
    }

    private static string? GetSectionFolderName(string filePath, string projectDirectoryName)
    {
        var dir = Path.GetDirectoryName(filePath);
        while (!string.IsNullOrEmpty(dir))
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent != null && string.Equals(Path.GetFileName(parent), projectDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                return GetSafePathName(dir);
            }

            dir = parent;
        }

        return null;
    }

    private static string GetSafePathName(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    /// <summary>Название команды по коду из каталога; неизвестный код отображается как есть.</summary>
    private static string GetCommandDisplayName(string code)
    {
        var definition = CommandCatalog.All.FirstOrDefault(
            c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));
        return definition.Name ?? code;
    }
}
