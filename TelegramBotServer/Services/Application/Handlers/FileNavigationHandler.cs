using Microsoft.Extensions.Options;
using TelegramBotServer.Config;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services.Application.Handlers;

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
        var isGoToParent = context.ParsedCallback.Is(CallbackPrefixes.GoToParent);

        session.AddToPagesCache(session.Counter);
        session.Counter = 0;

        if (session.SelectionType == SelectionMode.Sections)
            session.Level = !isGoToParent;

        if (isGoToParent)
        {
            session.RemoveLastFromPagesCache();
            session.Counter = session.GetLastPageFromCache();
        }

        var token = context.ParsedCallback.Argument;
        if (!_fileNavigationService.TryResolvePath(context.UserId, token, out var newPath) || newPath == null)
        {
            await _outputService.AnswerCallbackAsync(context.CallbackQueryId, "Действие устарело. Пожалуйста, начните заново (/start)", showAlert: true);
            return true;
        }

        session.CurrentPath = session.SelectionType switch
        {
            SelectionMode.Sections when !isGoToParent => _options.GetProjectPath(newPath),
            SelectionMode.Sections when isGoToParent => _options.RootPath,
            _ => newPath
        };

        await _outputService.AnswerCallbackAsync(context.CallbackQueryId, session.CurrentPath);

        var keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(context.UserId, session);
        await _outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);

        return true;
    }
}
