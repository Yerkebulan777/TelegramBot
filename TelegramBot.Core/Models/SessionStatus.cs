namespace TelegramBot.Core.Models;

/// <summary>Статус сессии (количество файлов, выполнено).</summary>
public class SessionStatus
{
    public string Status { get; set; } = string.Empty;
    public int TotalFiles { get; set; }
    public int DoneFiles { get; set; }
}
