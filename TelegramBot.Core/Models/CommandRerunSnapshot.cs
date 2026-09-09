namespace TelegramBot.Core.Models;

/// <summary>Неизменяемые данные завершённой команды для создания нового задания.</summary>
public sealed class CommandRerunSnapshot
{
    public int SourceCommandId { get; set; }
    public required string CommandText { get; set; }
    public required string FilePath { get; set; }
    public required string RootPath { get; set; }
    public int Priority { get; set; }
    public string? ProjectName { get; set; }
}
