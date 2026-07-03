namespace TelegramBot.Server.Services.Application.Strategies;

/// <summary>
/// Стратегия выполнения команды.
/// </summary>
public enum CommandStrategy
{
    Start,
    AccessDenied,
    CommandSelectionAction,
    SlashCommand,
}
