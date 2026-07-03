using Telegram.Bot.Exceptions;
using TelegramBot.Core.DTOs;
using TelegramBot.Core.Models;
using TelegramBot.Server.Interfaces;
using TelegramBot.Server.Services.Application.Handlers;

namespace TelegramBot.Server.Services.Application.Strategies;

/// <summary>
/// Результат выполнения команды.
/// </summary>
public sealed class CommandExecutionResult
{
    public bool IsHandled { get; }
    public string? ResponseMessage { get; }

    private CommandExecutionResult(bool isHandled, string? responseMessage = null)
    {
        IsHandled = isHandled;
        ResponseMessage = responseMessage;
    }

    public static CommandExecutionResult Handled() => new(true);
    public static CommandExecutionResult AccessDenied() => new(false, "У вас нет доступа. Введите /start для запроса доступа.");
}
