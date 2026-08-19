namespace TelegramBot.Core.Models;

/// <summary>Детальный статус сессии.</summary>
public class SessionStatus
{
    public string Status { get; set; } = string.Empty;
    public string? ProjectName { get; set; }
    public DateTime CreatedAt { get; set; }
}
