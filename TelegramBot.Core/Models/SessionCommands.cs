namespace TelegramBot.Core.Models;

/// <summary>Команда внутри сессии (порядок, команда, файл, статус).</summary>
public class SessionCommands
{
    public int ExecOrder { get; set; }
    public string Command { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public int CommandId { get; set; }
}
