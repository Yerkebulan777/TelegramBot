namespace TelegramBotServer.Models
{
    public class SessionStatus
    {
        public string Status { get; set; } = string.Empty;
        public int TotalFiles { get; set; }
        public int DoneFiles { get; set; }
    }
}
