namespace TelegramBotServer.Models;

/// <summary>Команда пользователя (экспорт/автоматизация для файла).</summary>
public class Command
{
    public int Id { get; set; }
    public long UserId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string CommandText { get; set; } = string.Empty;
    public string Status { get; set; } = "queued";
    public DateTime Timestamp { get; set; }
}