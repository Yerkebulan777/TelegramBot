namespace TelegramBot.Core.Models;

/// <summary>Раздел файловой системы (папка), доступная пользователю для выбора.</summary>
public class FileSystemItem
{
    public required string FullPath { get; set; }
}
