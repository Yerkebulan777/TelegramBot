namespace TelegramBot.Core.Models;

/// <summary>Элемент списка сессий с краткой сводкой статусов.</summary>
public class SessionsList
{
    public int SessionId { get; set; }
    public long UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public string Status { get; set; } = string.Empty;
    public int TotalCommands { get; set; }
    public int DoneCommands { get; set; }
    public int FailedCommands { get; set; }
    public int ActiveCommands { get; set; }
}
