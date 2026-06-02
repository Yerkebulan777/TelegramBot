using Microsoft.Extensions.Options;
using System.Text;
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
        IKeyboardBuilder keyboardBuilder,
        ITelegramOutputService outputService,
        IDataService dataService,
        IOptions<FileSystemOptions> options,
        ILogger<FileSelectionHandler> logger) : base(logger)
    {
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
        var pathMap = context.Session.PathMap;
        if (!pathMap.TryGetValue(token, out var filePath) || filePath == null)
        {
            await _outputService.SendErrorAsync(context.UserId, "File not found.");
            return true;
        }

        bool wasSelected = context.Session.SelectedFiles.Contains(filePath);
        context.Session.ToggleSelectedFile(filePath);

        Logger.LogInformation("User {Username} ({UserId}) {Action} section '{File}' (total selected: {Count})",
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
        var selectedSections = session.SelectedFiles;

        if (selectedSections.Count == 0)
            return true;

        Logger.LogInformation(
            "User {Username} ({UserId}) submitting job: commands=[{Commands}], selectedSections={SelectedCount}",
            context.Username, context.UserId, string.Join(", ", session.PendingCommand),
            selectedSections.Count);

        var filesToProcess = await MapSectionsToRvtFilesAsync(selectedSections, cancellationToken);

        Logger.LogInformation(
            "User {Username} ({UserId}) job resolved to {FileCount} RVT files",
            context.Username, context.UserId, filesToProcess.Count);

        await _dataService.CreateSessionWithCommandsAsync(
            session.PendingCommand, filesToProcess, context.UserId, context.Username, filesToProcess.Count);

        Logger.LogInformation("Job saved to DB for user {Username} ({UserId})", context.Username, context.UserId);

        var reply = BuildQueueReply(session);
        await _outputService.EditMessageReplyTextAsync(context.UserId, context.MessageId, reply);

        session.ResetNavigation(_options.RootPath);
        session.IsFileSelectionActive = false;

        return true;
    }

    private async Task<bool> HandleCancelFileSelectionAsync(CallbackContext context, CancellationToken cancellationToken)
    {
        var session = context.Session;
        session.FileSelectionMessageId = context.MessageId;

        Logger.LogInformation("User {Username} ({UserId}) cancelled file selection", context.Username, context.UserId);

        session.ResetNavigation(_options.RootPath);
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

    private async Task<List<string>> MapSectionsToRvtFilesAsync(
        IReadOnlySet<string> sections, CancellationToken cancellationToken)
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

            foreach (var file in Directory.EnumerateFiles(rvtDir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_options.IsRevitFile(file))
                    allFiles.Add(file);
            }
        }

        return allFiles;
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
