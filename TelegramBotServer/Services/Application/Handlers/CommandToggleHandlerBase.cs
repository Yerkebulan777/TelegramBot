using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services.Application.Handlers;

/// <summary>
/// Базовый класс для обработчиков, переключающих команды в сессии пользователя.
/// </summary>
public abstract class CommandToggleHandlerBase(
    IKeyboardBuilder keyboardBuilder,
    ITelegramOutputService outputService,
    ILogger logger) : CallbackHandlerBase(logger)
{
    private readonly IKeyboardBuilder _keyboardBuilder = keyboardBuilder;
    private readonly ITelegramOutputService _outputService = outputService;

    /// <summary>
    /// Сопоставление префикса команды с (код, отображаемое имя).
    /// </summary>
    protected abstract IReadOnlyDictionary<string, (string Code, string DisplayName)> Commands { get; }

    /// <inheritdoc/>
    public override bool CanHandle(string prefix) => Commands.ContainsKey(prefix);

    /// <summary>
    /// Возвращает клавиатуру для отображения после переключения.
    /// </summary>
    protected abstract Task<InlineKeyboardMarkup> GetKeyboardAsync(IKeyboardBuilder keyboardBuilder, UserSession session);

    protected override async Task<bool> HandleAsyncInternal(CallbackContext context, CancellationToken cancellationToken = default)
    {
        var (code, displayName) = Commands[context.ParsedCallback.Prefix];

        if (context.Session.ContainsPendingCommand(code))
        {
            context.Session.RemovePendingCommand(code);
            Logger.LogInformation("User {Username} ({UserId}) deselected command '{Code}'", context.Username, context.UserId, code);
        }
        else
        {
            context.Session.AddPendingCommand(code, displayName);
            Logger.LogInformation("User {Username} ({UserId}) selected command '{Code}' (pending: [{Commands}])",
                context.Username, context.UserId, code, string.Join(", ", context.Session.PendingCommand));
        }

        var keyboard = await GetKeyboardAsync(_keyboardBuilder, context.Session);
        await _outputService.EditMessageReplyMarkupAsync(context.UserId, context.MessageId, keyboard);

        return true;
    }
}
