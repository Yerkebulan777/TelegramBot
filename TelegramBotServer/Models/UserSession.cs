using System.Collections.Concurrent;

namespace TelegramBotServer.Models
{
    public class UserSession
    {
        //public string? UserName { get; set; }
        public long UserId { get; set; }
        public string State { get; set; } = "Idle";
        public string? TempData { get; set; } = null;
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;


        public string CurrentPath { get; set; } = Directory.GetCurrentDirectory();
        // ConcurrentDictionary: token-to-path mapping used by FileSystemBrowser and handlers
        public ConcurrentDictionary<string, string> PathMap { get; set; } = new();


        public List<string> PendingCommand { get; set; } = new();
        public List<string> PendingCommandName { get; set; } = new();
        public HashSet<string> SelectedFiles { get; set; } = new();
        public Dictionary<string, string> CommandParams { get; set; } = new();

        public int SelectionType { get; set; } = 1;
        public string? RootPath { get; set; }
        public bool Level { get; set; }
        public int Counter { get; set; } = 0;
        public List<int> PagesCache { get; set; } = new List<int>();

        public List<FileSystemItem> Items = new List<FileSystemItem>();

        public bool statusLevel { get; set; }
        public int sessionId { get; set; }
    }
}
