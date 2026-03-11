namespace TelegramBotServer.DTOs
{
    public class MessageDto
    {
        public long UserId { get; set; }
        public string? Username { get; set; }
        public long ChatId { get; set; }
        public string? Text { get; set; }
        public DateTime Date { get; set; }
        public int MessageId { get; set; }
    }
}
