using Serilog;
using Serilog.Events;
using TelegramBot.Core.Config;
using TelegramBot.Core.Interfaces;
using TelegramBot.Data;
using TelegramBot.Worker.Services;

namespace TelegramBot.Worker;

public static class Program
{
    public static async Task Main(string[]? args)
    {
        var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var logPath = Path.Combine(documentsPath, "TelegramBot", "Logs", "Worker", "log-.txt");

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
                .ConfigureServices((context, services) =>
                {
                    _ = services.AddSingleton<IDataService, PostgresDataService>();

                    _ = services.Configure<WorkerOptions>(context.Configuration.GetSection(WorkerOptions.SectionName));

                    _ = services.AddHostedService<CommandExecutionService>();
                })
                .UseSerilog((context, services, loggerConfiguration) =>
                {
                    var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    var logPath = Path.Combine(documentsPath, "TelegramBot", "Logs", "Worker", "log-.txt");

                    loggerConfiguration
                        .ReadFrom.Configuration(context.Configuration)
                        .ReadFrom.Services(services)
                        .Enrich.FromLogContext()
                        .WriteTo.File(logPath, rollingInterval: RollingInterval.Day);
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
