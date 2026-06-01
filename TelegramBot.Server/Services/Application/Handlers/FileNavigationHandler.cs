using Microsoft.Extensions.Options;
using TelegramBot.Core.Config;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Application.Handlers;

/// <summary>
/// Обработчик навигации по файловой системе (открыть папку, перейти к родителю).
/// </summary>
public sealed class FileNavigationHandler : CallbackHandlerBase
{
    private readonly IFileSystemBrowser _fileNavigationService;
    private readonly IKeyboardBuilder _keyboardBuilder;
    private readonly ITelegramOutputService _outputService;
    private readonly FileSystemOptions _options;

    protected override HashSet<string> SupportedPrefixes { get; } =
    [
        CallbackPrefixes.OpenFolder,
        CallbackPrefixes.GoToParent
    ];

    public override int Priority => 10;

    public FileNavigationHandler(
        IFileSystemBrowser fileNavigationService,
        IKeyboardBuilder keyboardBuilder,
        ITelegramOutputService outputService,
        IOptions<FileSystemOptions> options,
        ILogger<FileNavigationHandler> logger) : base(logger)
    {
        _fileNavigationService = fileNavigationService;
        _keyboardBuilder = keyboardBuilder;
        _outputService = outputService;
        _options = options.Value;
    }

    protected override async Task<bool> HandleAsyncInternal(CallbackContext context, CancellationToken cancellationToken = default)
    {
        var session = context.Session;
        session.FileSelectionMessageId = context.MessageId;
        var isGoToParent = context.ParsedCallback.Is(CallbackPrefixes.GoToParent);

        session.AddToPagesCache(session.Counter);
        session.Counter = 0;

        if (session.SelectionType == SelectionMode.Sections)
            session.IsNavigatingDeep = !isGoToParent;

        if (isGoToParent)
        {
            session.RemoveLastFromPagesCache();
            session.Counter = session.GetLastPageFromCache();
        }

        var token = context.ParsedCallback.Argument;
        if (!_fileNavigationService.TryResolvePath(context.UserId, token, out var newPath) || newPath == null)
        {
            await _outputService.SendErrorAsync(context.UserId, "Path not found.");
            return true;
        }

        var targetPath = session.SelectionType switch
        {
            SelectionMode.Sections when !isGoToParent => PathContainsSegment(newPath, _options.ProjectDirectoryName)
                ? newPath
                : _options.GetProjectPath(newPath),
            SelectionMode.Sections when isGoToParent => _options.RootPath,
            _ => GetFilesTargetPath(newPath, isGoToParent)
        };

        if (!IsPathWithinRoot(targetPath))
        {
            Logger.LogWarning("Rejected navigation outside root. User={Username} ({UserId}), Path={Path}", context.Username, context.UserId, targetPath);
            await _outputService.SendErrorAsync(context.UserId, "Недопустимый путь.");
            session.CurrentPath = _options.RootPath;
            return true;
        }

        session.CurrentPath = targetPath;

        Logger.LogInformation("User {Username} ({UserId}) navigated {Direction} to '{Path}'",
            context.Username, context.UserId, isGoToParent ? "up" : "into", targetPath);

        await _outputService.AnswerCallbackAsync(context.CallbackQueryId, session.CurrentPath);

        var keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(context.UserId, session);
        await _outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);

        return true;
    }

    private static bool PathContainsSegment(string path, string segment) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(s => s.Equals(segment, StringComparison.OrdinalIgnoreCase));

    private bool IsPathWithinRoot(string path)
    {
        try
        {
            var rootFullPath = Path.GetFullPath(_options.RootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidateFullPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return candidateFullPath.Equals(rootFullPath, StringComparison.OrdinalIgnoreCase)
                   || candidateFullPath.StartsWith(
                       rootFullPath + Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to validate path against root");
            return false;
        }
    }

    private bool IsProjectDirectory(string path)
    {
        try
        {
            var parent = Directory.GetParent(path);
            if (parent == null)
                return false;

            var rootFullPath = Path.GetFullPath(_options.RootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parentFullPath = Path.GetFullPath(parent.FullName)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return parentFullPath.Equals(rootFullPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private string GetFilesTargetPath(string newPath, bool isGoToParent)
    {
        if (IsProjectDirectory(newPath))
        {
            if (isGoToParent)
            {
                return _options.RootPath;
            }
            else
            {
                var projectPath = _options.GetProjectPath(newPath);
                if (Directory.Exists(projectPath))
                {
                    return projectPath;
                }
            }
        }
        return newPath;
    }
}
