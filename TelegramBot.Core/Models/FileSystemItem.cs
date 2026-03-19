namespace TelegramBot.Core.Models;

/// <summary>Тип элемента файловой системы.</summary>
public enum ItemType
{
    Directory,
    File
}

/// <summary>Элемент файловой системы (файл или папка).</summary>
public class FileSystemItem
{
    public required string FullPath { get; set; }
    public ItemType Type { get; set; }
}
