using System.Runtime.Versioning;
using Serilog;
using TelegramBot.BimLib.Config;
using TelegramBot.BimLib.Extensions;
using TelegramBot.Core.Config;
using TelegramBot.Core.Helpers;
using TelegramBot.Core.Interfaces;
using TelegramBot.Data;
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
                    config.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
                    config.AddEnvironmentVariables();
                })
                .ConfigureServices((context, services) =>
                {
                    services.AddSingleton<IDataService, PostgresDataService>();

                    services.Configure<WorkerOptions>(context.Configuration.GetSection(WorkerOptions.SectionName));
                    services.Configure<BimIntegrationOptions>(context.Configuration.GetSection(BimIntegrationOptions.SectionName));

                    services.AddBimIntegration();

                    services.AddHostedService<CommandExecutionService>();
                    services.AddHostedService<HealthCheckServer>();
                })
                .UseSerilog((context, services, loggerConfiguration) =>
                    {
                        SerilogSetup.ConfigureFileLogging(context.Configuration, services, loggerConfiguration, "Worker");

                        // Отдельный файл для BIM-специфичных логов (Revit, Navisworks — TelegramBot.BimLib.*)
                        loggerConfiguration.WriteTo.Logger(lc => lc
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
            Log.CloseAndFlush();
        }
    }
}
