using Serilog;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TelegramBot.Data;
using TelegramBot.Server.Extensions;

namespace TelegramBot.Server;

/// <summary>
/// Точка входа приложения.
/// Предназначено только для операционной системы Windows.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Program
{
    public static async Task Main(string[]? args)
    {
        // Проверка платформы во время выполнения (дополнительная защита)
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException(
                "Это приложение разработано исключительно для операционной системы Windows.");
        }

        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateBootstrapLogger();

        try
        {
            IHost host = Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((context, config) =>
                {
                    config.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
                    config.AddEnvironmentVariables();
                })
                .ConfigureServices((context, services) => _ = services.AddTelegramBotServer(context.Configuration))
                .UseSerilog((context, services, loggerConfiguration) => loggerConfiguration
                    .ReadFrom.Configuration(context.Configuration)
                    .ReadFrom.Services(services)
                    .Enrich.FromLogContext())
                .Build();

            await host.InitializeDatabaseAsync();
            await host.SeedAdminUsersAsync();
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
