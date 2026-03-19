using TelegramBot.Core.DTOs;
using TelegramBot.Core.Models;

namespace TelegramBot.Core.Interfaces;

/// <summary>
/// Определяет обработчик для callback-запросов Telegram.
/// </summary>
public interface ICallbackHandler
{
    /// <summary>Проверяет, может ли обработчик обработать callback.</summary>
    bool CanHandle(string prefix);

    /// <summary>Приоритет обработчика (меньше = раньше).</summary>
    int Priority => 100;

    /// <summary>Обрабатывает callback асинхронно.</summary>
    Task<bool> HandleAsync(CallbackContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Контекст для обработки callback-запроса.
/// </summary>
public sealed class CallbackContext
{
    public required long UserId { get; init; }
    public required long ChatId { get; init; }
    public required int MessageId { get; init; }
    public required string Username { get; init; }
    public required string CallbackQueryId { get; init; }
    public required ParsedCallback ParsedCallback { get; init; }
    public required UserSession Session { get; init; }
    public List<List<ButtonDto>>? Buttons { get; init; }
}
