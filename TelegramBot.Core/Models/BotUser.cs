namespace TelegramBot.Core.Models;

public class BotUser
{
    public long UserId { get; set; }
    public string? Username { get; set; }
    public UserRole Role { get; set; }
    public UserAccessStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
