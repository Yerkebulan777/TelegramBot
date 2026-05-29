using Serilog;
using TelegramBot.Data;
using TelegramBot.Server.Extensions;

namespace TelegramBot.Server;

public class Program
{
    public static void Main(string[]? args)
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateBootstrapLogger();

        try
        {
            IHost host = Host.CreateDefaultBuilder(args)
                .ConfigureServices((context, services) => _ = services.AddTelegramBotServer(context.Configuration))
                .UseSerilog((context, services, loggerConfiguration) => loggerConfiguration
                    .ReadFrom.Configuration(context.Configuration)
                    .ReadFrom.Services(services)
                    .Enrich.FromLogContext())
                .Build();

            host.InitializeDatabaseAsync().GetAwaiter().GetResult();
            host.SeedAdminUsersAsync().GetAwaiter().GetResult();
            host.Run();
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
