using Serilog;
using TelegramBot.Core.Config;
using TelegramBot.Core.Helpers;
using TelegramBot.Core.Interfaces;
using TelegramBot.Data;
using TelegramBot.Worker.Services;

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
                    _ = services.AddSingleton<IDataService, PostgresDataService>();

                    _ = services.Configure<WorkerOptions>(context.Configuration.GetSection(WorkerOptions.SectionName));

                    _ = services.AddHostedService<CommandExecutionService>();
                })
                .UseSerilog((context, services, loggerConfiguration) =>
                    SerilogSetup.ConfigureFileLogging(context.Configuration, services, loggerConfiguration, "Worker"))
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
