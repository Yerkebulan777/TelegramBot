namespace TelegramBot.Core.Models;

/// <summary>Команда внутри сессии.</summary>
public class SessionCommands
{
    public string Command { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int CommandId { get; set; }
}
