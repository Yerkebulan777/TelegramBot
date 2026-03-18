using Telegram.Bot;
using Telegram.Bot.Types;

namespace TelegramBotServer.Config
{
    public class Config
    {
        public static async Task ConfigureAsync(ITelegramBotClient bot, ILogger logger)
        {
            BotCommand[] commands = new[]
            {
                new BotCommand { Command = "export", Description = "Export to different formats" },
                new BotCommand { Command = "automation", Description = "Automation features" },
                new BotCommand { Command = "status", Description = "Check your command queue" },
                new BotCommand { Command = "help", Description = "Show help menu" }
            };

            logger.LogInformation("Configuring Telegram bot commands. Count: {Count}", commands.Length);

            try
            {
                await bot.SetMyCommands(commands);
                logger.LogInformation("Telegram bot commands configured successfully.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to configure Telegram bot commands.");
                throw;
            }
        }
    }
}
