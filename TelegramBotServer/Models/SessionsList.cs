namespace TelegramBotServer.Models
{
    /// <summary>Элемент списка сессий (ID и дата).</summary>
    public class SessionsList
    {
        public int SessionId { get; set; }
        public DateTime Date { get; set; }
    }
}
