namespace TelegramBot.Data.Models;

/// <summary>
/// Идентификатор сообщения Telegram, сохранённый для последующего удаления.
/// </summary>
public sealed record TrackedMessageReference(long ChatId, int MessageId);
