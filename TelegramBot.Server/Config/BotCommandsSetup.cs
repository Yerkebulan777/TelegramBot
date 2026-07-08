using Telegram.Bot;
using Telegram.Bot.Types;

namespace TelegramBot.Server.Config;

public static class BotCommandsSetup
{
    public static async Task ConfigureAsync(ITelegramBotClient bot, ILogger logger)
    {
        BotCommand[] commands =
        [
            new BotCommand { Command = "export", Description = "Export to different formats" },
            new BotCommand { Command = "automation", Description = "Automation features" },
            new BotCommand { Command = "status", Description = "Check your command queue" },
            new BotCommand { Command = "help", Description = "Show help menu" }
        ];

        logger.LogInformation("Configuring bot commands: count={Count}", commands.Length);

        try
        {
            await bot.SetMyCommands(commands);
            logger.LogInformation("Bot commands configured");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Configure bot commands fail");
            throw;
        }
    }
}
