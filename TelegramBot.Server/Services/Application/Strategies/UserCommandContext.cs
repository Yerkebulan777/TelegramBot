using TelegramBot.Core.DTOs;
using TelegramBot.Core.Models;
using TelegramBot.Server.Middleware;

namespace TelegramBot.Server.Services.Application.Strategies;

/// <summary>
/// Контекст команды пользователя.
/// </summary>
public sealed class UserCommandContext
{
    public MessageDto Message { get; }
    public UserSession Session { get; }
    public long UserId { get; }
    public long ChatId { get; }
    public string RawText { get; }
    public string Command { get; }
    public string Username { get; }
    public BotUser? User { get; }
    public bool IsActive { get; }

    public UserCommandContext(
        MessageDto message,
        UserSession session,
        long userId,
        long chatId,
        string rawText,
        string command,
        string username,
        BotUser? user,
        bool isActive)
    {
        Message = message;
        Session = session;
        UserId = userId;
        ChatId = chatId;
        RawText = rawText;
        Command = command;
        Username = username;
        User = user;
        IsActive = isActive;
    }
}
