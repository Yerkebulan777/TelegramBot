using Microsoft.Extensions.Options;
using System.Text;
using System.Text.RegularExpressions;
using TelegramBot.Core.Config;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Application.Handlers;

/// <summary>
/// Обработчик операций выбора файлов (переключение, применение, отмена).
/// </summary>
public sealed class FileSelectionHandler : CallbackHandlerBase
{
    private readonly IFileSystemBrowser _fileNavigationService;
    private readonly IKeyboardBuilder _keyboardBuilder;
    private readonly ITelegramOutputService _outputService;
    private readonly IDataService _dataService;
    private readonly FileSystemOptions _options;

    protected override HashSet<string> SupportedPrefixes { get; } =
    [
        CallbackPrefixes.File,
        CallbackPrefixes.ApplyFiles,
        CallbackPrefixes.CancelFileSelection
    ];

    public override int Priority => 20;

    public FileSelectionHandler(
        IFileSystemBrowser fileNavigationService,
        IKeyboardBuilder keyboardBuilder,
        ITelegramOutputService outputService,
        IDataService dataService,
        IOptions<FileSystemOptions> options,
        ILogger<FileSelectionHandler> logger) : base(logger)
    {
        _fileNavigationService = fileNavigationService;
        _keyboardBuilder = keyboardBuilder;
        _outputService = outputService;
        _dataService = dataService;
        _options = options.Value;
    }

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
        context.Session.FileSelectionMessageId = context.MessageId;

        var token = context.ParsedCallback.Argument;
        if (!_fileNavigationService.TryResolvePath(context.UserId, token, out var filePath) || filePath == null)
        {
            await _outputService.SendErrorAsync(context.UserId, "File not found.");
            return true;
        }

        bool wasSelected = context.Session.SelectedFiles.Contains(filePath);
        context.Session.ToggleSelectedFile(filePath);

        Logger.LogInformation("User {Username} ({UserId}) {Action} file '{File}' (total selected: {Count})",
            context.Username, context.UserId, wasSelected ? "deselected" : "selected",
            Path.GetFileName(filePath), context.Session.SelectedFiles.Count);

        var keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(context.UserId, context.Session);
        await _outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);

        return true;
    }

    private async Task<bool> HandleApplyFilesAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var session = context.Session;
        session.FileSelectionMessageId = context.MessageId;
        var selectedFiles = session.SelectedFiles;

        if (selectedFiles.Count == 0)
            return true;

        Logger.LogInformation(
            "User {Username} ({UserId}) submitting job: commands=[{Commands}], selectionMode={Mode}, selectedItems={SelectedCount}",
            context.Username, context.UserId, string.Join(", ", session.PendingCommand),
            session.SelectionType, selectedFiles.Count);

        var filesToProcess = await MapFilesAsync(selectedFiles, session.SelectionType, cancellationToken);

        Logger.LogInformation(
            "User {Username} ({UserId}) job resolved to {FileCount} files after mapping (mode={Mode})",
            context.Username, context.UserId, filesToProcess.Count, session.SelectionType);

        await _dataService.CreateSessionWithCommandsAsync(
            session.PendingCommand, filesToProcess, context.UserId, context.Username,
            (int)session.SelectionType, filesToProcess.Count);

        Logger.LogInformation("Job saved to DB for user {Username} ({UserId})", context.Username, context.UserId);

        var reply = BuildQueueReply(session);
        await _outputService.EditMessageReplyTextAsync(context.UserId, context.MessageId, reply);

        session.ResetNavigation(_options.RootPath);
        session.SelectionType = SelectionMode.Files;
        session.IsFileSelectionActive = false;

        return true;
    }

    private async Task<bool> HandleCancelFileSelectionAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var session = context.Session;
        session.FileSelectionMessageId = context.MessageId;

        Logger.LogInformation("User {Username} ({UserId}) cancelled file selection", context.Username, context.UserId);

        session.ResetNavigation(_options.RootPath);
        session.SelectionType = SelectionMode.Files;
        session.IsFileSelectionActive = false;

        if (CommandCodes.ExportCodes.Any(session.ContainsPendingCommand))
        {
            var keyboard = await _keyboardBuilder.GetCommandsKeyboardAsync(session);
            await _outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);
        }
        else if (CommandCodes.AutomationCodes.Any(session.ContainsPendingCommand))
        {
            var keyboard = await _keyboardBuilder.GetAutomationKeyboardAsync(session);
            await _outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);
        }

        return true;
    }

    private async Task<List<string>> MapFilesAsync(
        IReadOnlySet<string> selectedFiles, SelectionMode selectionMode, CancellationToken cancellationToken)
    {
        return selectionMode switch
        {
            SelectionMode.Sections => await MapSectionsToFilesAsync(selectedFiles, cancellationToken),
            SelectionMode.Projects => await MapProjectsToFilesAsync(selectedFiles, cancellationToken),
            _ => selectedFiles.ToList()
        };
    }

    private async Task<List<string>> MapSectionsToFilesAsync(
        IEnumerable<string> sections, CancellationToken cancellationToken)
    {
        var allFiles = new List<string>();

        foreach (var section in sections)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rvtDir = _options.GetRvtPath(section);
            if (!Directory.Exists(rvtDir))
            {
                Logger.LogWarning("RVT directory not found: {RvtDir}", rvtDir);
                continue;
            }

            AddRevitFilesFromDirectory(rvtDir, allFiles, cancellationToken);
        }

        return allFiles;
    }

    private async Task<List<string>> MapProjectsToFilesAsync(
        IEnumerable<string> projects, CancellationToken cancellationToken)
    {
        var allFiles = new List<string>();
        var roman3 = new Regex(_options.RomanThreePattern, RegexOptions.IgnoreCase);

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var projectDir = _options.GetProjectPath(project);
            if (!Directory.Exists(projectDir))
            {
                Logger.LogWarning("Project directory not found: {ProjectDir}", projectDir);
                continue;
            }

            var sections = Directory.GetDirectories(projectDir)
                .Where(d => roman3.IsMatch(Path.GetFileName(d)));

            foreach (var section in sections)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var rvtDir = _options.GetRvtPath(section);
                if (!Directory.Exists(rvtDir))
                    continue;

                AddRevitFilesFromDirectory(rvtDir, allFiles, cancellationToken);
            }
        }

        return allFiles;
    }

    private void AddRevitFilesFromDirectory(string directory, List<string> files, CancellationToken cancellationToken)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_options.IsRevitFile(file))
                files.Add(file);
        }
    }

    private static string BuildQueueReply(UserSession session)
    {
        var sb = new StringBuilder("Команда:\n");
        foreach (var cmd in session.PendingCommandName)
            sb.Append("\u2705 ").Append(cmd).Append('\n');

        sb.Append("Добавлены файлы:\n");
        foreach (var file in session.SelectedFiles)
            sb.Append("\u2705 ").Append(Path.GetFileName(file)).Append('\n');

        sb.Append("\n/status для проверки статуса команды");
        return sb.ToString();
    }
}
