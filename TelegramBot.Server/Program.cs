using Serilog;
using Serilog.Events;
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

        var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var logPath = Path.Combine(documentsPath, "TelegramBot", "Logs", "Server", "log-.txt");

        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
            .CreateBootstrapLogger();

        try
        {
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((context, config) =>
                {
                    _=config.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
                    _=config.AddEnvironmentVariables();
                })
                .ConfigureServices((context, services) => _ = services.AddTelegramBotServer(context.Configuration))
                .UseSerilog((context, services, loggerConfiguration) =>
                {
                    var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    var logPath = Path.Combine(documentsPath, "TelegramBot", "Logs", "Server", "log-.txt");

                    loggerConfiguration
                        .ReadFrom.Configuration(context.Configuration)
                        .ReadFrom.Services(services)
                        .Enrich.FromLogContext()
                        .WriteTo.File(logPath, rollingInterval: RollingInterval.Day);
                })
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
