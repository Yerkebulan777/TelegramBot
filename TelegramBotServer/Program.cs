using Telegram.Bot;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Services;

namespace TelegramBotServer;

public class Program
{
    public static async Task Main(string[]? args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((context, cfg) =>
            {
                cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                cfg.AddEnvironmentVariables();
                if (args != null) cfg.AddCommandLine(args);
            })
            .ConfigureServices((context, services) =>
            {
                // Configuration
                var configuration = context.Configuration;

                // Register Telegram client as singleton
                services.AddSingleton<ITelegramBotClient>(sp =>
                {
                    var token = "7590057279:AAGvtBT68sN1t4ikUaYus_LpZ-5zvQ03TV0";
                    return new TelegramBotClient(token);
                });

                // App services
                services.AddSingleton<IDataService, SqliteDataService>(); //edited
                services.AddSingleton<ITelegramOutputService, TelegramOutputService>();
                services.AddSingleton<INavigationService, NavigationService>();
                services.AddSingleton<ICommandAppService, CommandAppService>();
                services.AddSingleton<IAuthService, AuthService>();

                services.AddSingleton<ISessionManager>(sp => new SessionManager(TimeSpan.FromMinutes(5)));
                services.AddSingleton<ITelegramUpdateMapper, TelegramUpdateMapper>();
                services.AddSingleton<IKeyboardBuilder, KeyboardBuilder>();

                // Hosted service - bot polling runs inside BackgroundService
                services.AddHostedService<TelegramBotHostedService>();
                // Add logging, options etc. as needed
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
            })
            .Build();

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IDataService>();

            if (db is SqliteDataService sqlite)
            {
                await sqlite.InitializeDatabaseAsync();
            }
        }

        await host.RunAsync();
    }
}