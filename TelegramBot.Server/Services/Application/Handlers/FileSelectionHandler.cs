using Microsoft.Extensions.Options;
using System.Text;
using TelegramBot.Core.Config;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;
using TelegramBot.Server.Constants;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Application.Handlers;

public sealed class FileSelectionHandler(
    IKeyboardBuilder keyboardBuilder,
    ITelegramOutputService outputService,
    IDataService dataService,
    IOptions<FileSystemOptions> options,
    ILogger<FileSelectionHandler> logger) : CallbackHandlerBase(logger)
{
    private readonly IKeyboardBuilder _keyboardBuilder = keyboardBuilder;
    private readonly ITelegramOutputService _outputService = outputService;
    private readonly IDataService _dataService = dataService;
    private readonly FileSystemOptions _options = options.Value;

    protected override HashSet<string> SupportedPrefixes { get; } =
    [
        CallbackPrefixes.File,
        CallbackPrefixes.ApplyFiles,
        CallbackPrefixes.CancelFileSelection
    ];

    public override int Priority => HandlerPriorities.FileSelection;

    protected override async Task<bool> HandleAsyncInternal(CallbackContext context, CancellationToken cancellationToken = default)
    {
        return context.ParsedCallback.Prefix switch
        {
            CallbackPrefixes.File => await HandleFileToggleAsync(context, cancellationToken),
            CallbackPrefixes.ApplyFiles => await HandleApplyFilesAsync(context, cancellationToken),
            CallbackPrefixes.CancelFileSelection => await HandleCancelFileSelectionAsync(context, cancellationToken),
            _ => false
        };
    }

    private async Task<bool> HandleFileToggleAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var session = context.Session;
        session.FileSelectionMessageId = context.MessageId;

        if (!session.PathMap.TryGetValue(context.ParsedCallback.Argument, out var filePath) || filePath == null)
        {
            await _outputService.SendErrorAsync(context.UserId, "File not found.");
            return true;
        }

        if (IsAtProjectLevel(session))
        {
            // Одиночный выбор: сбросить предыдущий, выбрать новый
            session.ClearSelectedFiles();
            session.ToggleSelectedFile(filePath);
            Logger.LogInformation("User {Username} ({UserId}) selected project '{Project}'",
                context.Username, context.UserId, Path.GetFileName(filePath));
        }
        else
        {
            // Множественный выбор разделов
            bool wasSelected = session.SelectedFiles.Contains(filePath);
            session.ToggleSelectedFile(filePath);
            Logger.LogInformation("User {Username} ({UserId}) {Action} section '{Section}' (total: {Count})",
                context.Username, context.UserId, wasSelected ? "deselected" : "selected",
                Path.GetFileName(filePath), session.SelectedFiles.Count);
        }

        var keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(context.UserId, session);
        await _outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);
        await _outputService.AnswerCallbackAsync(context.CallbackQueryId, "");

        return true;
    }

    private async Task<bool> HandleApplyFilesAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var session = context.Session;
        session.FileSelectionMessageId = context.MessageId;

        if (IsAtProjectLevel(session))
        {
            // Подтверждение проекта → переход в 01_PROJECT
            var selectedProject = session.SelectedFiles.FirstOrDefault();
            if (selectedProject == null) return true;

            session.CurrentPath = Path.Combine(selectedProject, _options.ProjectDirectoryName);
            session.ClearSelectedFiles();

            Logger.LogInformation("User {Username} ({UserId}) confirmed project '{Project}', navigated to 01_PROJECT",
                context.Username, context.UserId, Path.GetFileName(selectedProject));

            await _outputService.AnswerCallbackAsync(context.CallbackQueryId, "");
            var keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(context.UserId, session);
            await _outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);
            await SendFileActionsReplyKeyboardAsync(context);
            return true;
        }

        // Подтверждение разделов → создание задания
        var selectedSections = session.SelectedFiles;
        if (selectedSections.Count == 0) return true;

        Logger.LogInformation(
            "User {Username} ({UserId}) submitting job: commands=[{Commands}], sections={Count}",
            context.Username, context.UserId, string.Join(", ", session.PendingCommand), selectedSections.Count);

        var filesToProcess = CollectRvtFiles(selectedSections, cancellationToken);

        Logger.LogInformation("User {Username} ({UserId}) job resolved to {FileCount} RVT files",
            context.Username, context.UserId, filesToProcess.Count);

        await _dataService.CreateSessionWithCommandsAsync(
            session.PendingCommand, filesToProcess, context.UserId, context.Username, filesToProcess.Count);

        Logger.LogInformation("Job saved to DB for user {Username} ({UserId})", context.Username, context.UserId);

        await _outputService.EditMessageReplyTextAsync(context.UserId, context.MessageId, BuildQueueReply(session));

        session.ResetNavigation(_options.RootPath);
        session.IsFileSelectionActive = false;
        session.ClearPendingCommands();

        var clearKeyboardMessage = await _outputService.RemoveReplyKeyboardAsync(context.UserId, "Задание добавлено в очередь.");
        if (clearKeyboardMessage != null)
            session.TrackMessage(clearKeyboardMessage.Id);

        return true;
    }

    private async Task<bool> HandleCancelFileSelectionAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        context.Session.Reset(_options.RootPath);
        await _outputService.AnswerCallbackAsync(context.CallbackQueryId, "Отменено");
        await _outputService.EditMessageReplyTextAsync(context.UserId, context.MessageId, "Выбор отменён.");
        var clearKeyboardMessage = await _outputService.RemoveReplyKeyboardAsync(context.UserId, "Выбор отменен.");
        if (clearKeyboardMessage != null)
            context.Session.TrackMessage(clearKeyboardMessage.Id);
        return true;
    }

    private async Task SendFileActionsReplyKeyboardAsync(CallbackContext context)
    {
        var replyKeyboard = IsAtProjectLevel(context.Session)
            ? await _keyboardBuilder.GetProjectActionsReplyKeyboardAsync()
            : await _keyboardBuilder.GetSectionActionsReplyKeyboardAsync();

        var message = await _outputService.SendMessageWithReplyKeyboardAsync(context.UserId, "Действия:", replyKeyboard);
        if (message != null)
            context.Session.TrackMessage(message.Id);
    }

    private List<string> CollectRvtFiles(IReadOnlySet<string> sectionPaths, CancellationToken cancellationToken)
    {
        var files = new List<string>();

        foreach (var sectionPath in sectionPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rvtDir = _options.GetRvtPath(sectionPath);
            if (!Directory.Exists(rvtDir))
            {
                Logger.LogWarning("RVT directory not found: {RvtDir}", rvtDir);
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(rvtDir))
            {
                if (_options.IsRevitFile(file))
                    files.Add(file);
            }
        }

        return files;
    }

    private bool IsAtProjectLevel(UserSession session) =>
        !string.Equals(Path.GetFileName(session.CurrentPath), _options.ProjectDirectoryName,
            StringComparison.OrdinalIgnoreCase);

    private static string BuildQueueReply(UserSession session)
    {
        var sb = new StringBuilder("Команда:\n");
        foreach (var cmd in session.PendingCommandName)
            sb.Append("✅ ").Append(cmd).Append('\n');

        sb.Append("Добавлены файлы:\n");
        foreach (var file in session.SelectedFiles)
            sb.Append("✅ ").Append(Path.GetFileName(file)).Append('\n');

        sb.Append("\n/status для проверки статуса команды");
        return sb.ToString();
    }
}
