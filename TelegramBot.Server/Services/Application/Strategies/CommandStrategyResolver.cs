using TelegramBot.Server.Models;

namespace TelegramBot.Server.Services.Application.Strategies;

/// <summary>
/// Определяет стратегию выполнения команды на основе контекста.
/// </summary>
public sealed class CommandStrategyResolver
{
    /// <summary>
    /// Определяет стратегию для данного контекста.
    /// </summary>
    public CommandStrategy Resolve(UserCommandContext context)
    {
        if (!context.IsActive)
        {
            return CommandStrategy.AccessDenied;
        }

        return context.Command switch
        {
            "/start" => CommandStrategy.Start,
            _ when IsCommandSelectionAction(context.RawText) => CommandStrategy.CommandSelectionAction,
            _ => CommandStrategy.SlashCommand
        };
    }

    private static bool IsCommandSelectionAction(string messageText)
    {
        return messageText is ButtonTexts.Apply or ButtonTexts.Cancel or ButtonTexts.Confirm;
    }
}
