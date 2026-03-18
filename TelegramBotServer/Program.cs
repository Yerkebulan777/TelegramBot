using Serilog;
using TelegramBotServer.Extensions;

namespace TelegramBotServer;

public class Program
{
    public static async Task Main(string[]? args)
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateBootstrapLogger();

        try
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
            .ConfigureServices((context, services) => _ = services.AddTelegramBotServer(context.Configuration))
            .UseSerilog((context, services, loggerConfiguration) => loggerConfiguration
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext())
            .Build();

            await host.InitializeDatabaseAsync();

            await host.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
        }
        finally
        {
            Log.CloseAndFlush();
        }

    }
}
