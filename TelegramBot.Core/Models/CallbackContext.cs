namespace TelegramBot.Core.Models;

/// <summary>
/// Контекст для обработки callback-запроса.
/// </summary>
public sealed class CallbackContext
{
    public required long UserId { get; init; }
    public required int MessageId { get; init; }
    public required string Username { get; init; }
    public required string CallbackQueryId { get; init; }
    public required ParsedCallback ParsedCallback { get; init; }
    public required UserSession Session { get; init; }
}
