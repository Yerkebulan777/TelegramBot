using Serilog;
using System.Runtime.Versioning;
using TelegramBot.Core.Config;
using TelegramBot.Core.Interfaces;
using TelegramBot.Data;
using TelegramBot.Worker.BimLib.Config;
using TelegramBot.Worker.BimLib.Interfaces;
using TelegramBot.Worker.BimLib.Monitor;
using TelegramBot.Worker.BimLib.Services;
using TelegramBot.Worker.Helpers;
using TelegramBot.Worker.Services;

[assembly: SupportedOSPlatform("windows")]

namespace TelegramBot.Worker;

public static class Program
{
    public static async Task Main(string[]? args)
    {
        SerilogSetup.ConfigureBootstrapLogger("Worker");

        try
        {
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((context, config) =>
                {
                    _=config.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
                    _=config.AddEnvironmentVariables();
                })
                .ConfigureServices((context, services) =>
                {
                    _=services.AddSingleton<IUserDataService, UserDataService>();
                    _=services.AddSingleton<CommandDataService>();
                    _=services.AddSingleton<ICommandDataService>(sp => sp.GetRequiredService<CommandDataService>());
                    _=services.AddSingleton<SessionDataService>();
                    _=services.AddSingleton<ISessionDataService>(sp => sp.GetRequiredService<SessionDataService>());
                    _=services.AddSingleton<INotificationDataService>(sp => sp.GetRequiredService<SessionDataService>());
                    _=services.AddSingleton<IMessageTrackingDataService, MessageTrackingDataService>();
                    _=services.AddSingleton<IDatabaseInitializer, DatabaseInitializerService>();

                    _=services.Configure<WorkerOptions>(context.Configuration.GetSection(WorkerOptions.SectionName));
                    _=services.Configure<BimIntegrationOptions>(context.Configuration.GetSection(BimIntegrationOptions.SectionName));

                    // BIM-интеграция (Revit + Navisworks)
                    _=services.AddSingleton<IRevitVersionDetector, RevitVersionDetector>();
                    _=services.AddSingleton<RevitPathResolver>();
                    _=services.AddSingleton<RevitProcessTracker>();
                    _=services.AddSingleton<DialogDismisser>();
                    _=services.AddSingleton<INavisworksPathResolver, NavisworksPathResolver>();
                    _=services.AddSingleton<NavisworksProcessTracker>();

                    _=services.AddHostedService<CommandExecutionService>();
                })
                .UseSerilog((context, services, loggerConfiguration) =>
                    {
                        SerilogSetup.ConfigureFileLogging(context.Configuration, services, loggerConfiguration, "Worker");

                        // Отдельный файл для BIM-специфичных логов (Revit, Navisworks — TelegramBot.BimLib.*)
                        _=loggerConfiguration.WriteTo.Logger(lc => lc
                            .MinimumLevel.Information()
                            .Enrich.FromLogContext()
                            .Filter.ByIncludingOnly(BimLibLogFilter.IsBimLibEvent)
                            .WriteTo.File(
                                SerilogSetup.GetLogPath(Path.Combine("Worker", "BimLib")),
                                rollingInterval: RollingInterval.Day));
                    })
                .Build();

            await host.InitializeDatabaseAsync();
            await host.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Worker terminated unexpectedly");
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }
}
