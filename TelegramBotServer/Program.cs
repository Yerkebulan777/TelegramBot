using Telegram.Bot;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Services;

namespace TelegramBotServer;

public class Program
{
    public static async Task Main(string[]? args)
    {
        IHost host = Host.CreateDefaultBuilder(args)

            .ConfigureAppConfiguration((context, cfg) =>
            {
                _ = cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                _ = cfg.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
                _ = cfg.AddEnvironmentVariables();
                if (args != null)
                {
                    _ = cfg.AddCommandLine(args);
                }
            })
            .ConfigureServices((context, services) =>
            {
                // Configuration
                IConfiguration configuration = context.Configuration;

                // Register Telegram client as singleton
                _ = services.AddSingleton<ITelegramBotClient>(sp =>
                {
                    var token = configuration["TelegramBot:Token"]
                        ?? throw new InvalidOperationException(
                            "TelegramBot:Token is not configured. Set it in appsettings.Local.json or via environment variable TelegramBot__Token.");
                    return new TelegramBotClient(token);
                });

                // App services
                _ = services.AddSingleton<IDataService, SqliteDataService>(); //edited
                _ = services.AddSingleton<ITelegramOutputService, TelegramOutputService>();
                _ = services.AddSingleton<IFileSystemBrowser, FileSystemBrowser>();
                _ = services.AddSingleton<ICommandAppService, CommandAppService>();
                _ = services.AddSingleton<IAuthService, AuthService>();

                _ = services.AddSingleton<ISessionManager>(sp => new SessionManager(TimeSpan.FromMinutes(5)));
                _ = services.AddSingleton<ITelegramUpdateMapper, TelegramUpdateMapper>();
                _ = services.AddSingleton<IKeyboardBuilder, KeyboardBuilder>();

                // Hosted service - bot polling runs inside BackgroundService
                _ = services.AddHostedService<TelegramBotHostedService>();
                // Add logging, options etc. as needed
            })
            .ConfigureLogging(logging =>
            {
                _ = logging.ClearProviders();
                _ = logging.AddConsole();
            })
            .Build();

        using (IServiceScope scope = host.Services.CreateScope())
        {
            IDataService db = scope.ServiceProvider.GetRequiredService<IDataService>();

            if (db is SqliteDataService sqlite)
            {
                await sqlite.InitializeDatabaseAsync();
            }
        }

        await host.RunAsync();
    }
}