using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TelegramBot.Core.Config;
using TelegramBot.Core.Helpers;
using TelegramBot.Core.Models;
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
            throw new PlatformNotSupportedException("Это приложение разработано исключительно для операционной системы Windows.");
        }

        SerilogSetup.ConfigureBootstrapLogger("Server");

        try
        {
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((context, config) =>
                {
                    _=config.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
                    _=config.AddEnvironmentVariables();
                })
                .ConfigureServices((context, services) => services.AddTelegramBotServer(context.Configuration))
                .UseSerilog((context, services, loggerConfiguration) =>
                    SerilogSetup.ConfigureFileLogging(context.Configuration, services, loggerConfiguration, "Server"))
                .Build();

            await host.Services.GetRequiredService<DatabaseInitializerService>().InitializeDatabaseAsync();

            var adminIds = host.Services.GetRequiredService<IOptions<BotOptions>>().Value.AdminUserIds;
            if (adminIds.Length > 0)
            {
                await host.Services.GetRequiredService<UserDataService>().UpsertUsersBatchAsync(
                    adminIds, (int)UserRole.Admin, (int)UserAccessStatus.Approved);
            }

            await host.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }
}
