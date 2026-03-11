namespace TelegramBotServer.Models
{
    public enum ItemType
    {
        Directory,
        File
    }

    public class FileSystemItem
    {
        public required string FullPath { get; set; }
        public ItemType Type { get; set; }
    }

}
