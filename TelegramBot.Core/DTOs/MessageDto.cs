namespace TelegramBot.Core.DTOs;

/// <summary>DTO для входящего текстового сообщения.</summary>
public class MessageDto
{
    public long UserId { get; set; }
    public long ChatId { get; set; }
    public string? Text { get; set; }
    public string? Username { get; set; }
    public int MessageId { get; set; }
}
