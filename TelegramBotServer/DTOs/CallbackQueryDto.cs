namespace TelegramBotServer.DTOs
{
    /// <summary>DTO для входящего callback-запроса.</summary>
    public class CallbackQueryDto
    {
        public long UserId { get; set; }
        public string? Username { get; set; }
        public long ChatId { get; set; }
        public string? MessageText { get; set; }
        public int MessageId { get; set; }
        public string? CallbackData { get; set; }
        public string? CallbackQueryId { get; set; }
        public List<List<ButtonDto>> Buttons { get; set; } = new();
    }
}
