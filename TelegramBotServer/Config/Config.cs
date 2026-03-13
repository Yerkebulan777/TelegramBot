using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace TelegramBotServer.Config
{
    public class Config
    {
        public static async Task ConfigureAsync(ITelegramBotClient bot, ILogger logger)
        {
            var commands = new[]
            {
                new BotCommand { Command = "/export", Description = "Export to different formats" },
                new BotCommand { Command = "/automation", Description = "Automation features" },
                new BotCommand { Command = "/status", Description = "Check your command queue" },
                new BotCommand { Command = "/help", Description = "Show help menu" }
            };

            await bot.SetMyCommands(commands);

            logger.LogInformation("Telegram bot commands configured.");
        }
    }
}
