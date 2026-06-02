using Microsoft.Extensions.Options;
using TelegramBot.Core.Config;
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

    protected override HashSet<string> SupportedPrefixes { get; } = [CallbackPrefixes.GoToParent];

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

        if (!_fileNavigationService.TryResolvePath(context.UserId, context.ParsedCallback.Argument, out var newPath) || newPath is null)
        {
            await _outputService.SendErrorAsync(context.UserId, "Path not found.");
            return true;
        }

        if (!_options.IsPathWithinRoot(newPath))
        {
            Logger.LogWarning("Rejected navigation outside root. User={Username} ({UserId}), Path={Path}",
                context.Username, context.UserId, newPath);
            await _outputService.SendErrorAsync(context.UserId, "Недопустимый путь.");
            session.CurrentPath = _options.RootPath;
            return true;
        }

        session.CurrentPath = newPath;
        Logger.LogInformation("User {Username} ({UserId}) navigated to '{Path}'",
            context.Username, context.UserId, newPath);

        await _outputService.AnswerCallbackAsync(context.CallbackQueryId, session.CurrentPath);

        var keyboard = await _keyboardBuilder.GetSelectionKeyboardAsync(context.UserId, session);
        await _outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);

        return true;
    }
}
