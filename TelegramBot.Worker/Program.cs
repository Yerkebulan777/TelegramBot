using Microsoft.Extensions.Options;
using Serilog;
using System.Runtime.Versioning;
using TelegramBot.Core.Config;
using TelegramBot.Core.Health;
using TelegramBot.Core.Helpers;
using TelegramBot.Data;
using TelegramBot.Worker.BimLib.Config;
using TelegramBot.Worker.BimLib.Monitor;
using TelegramBot.Worker.BimLib.Native;
using TelegramBot.Worker.BimLib.Services;
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
                    _=services.AddSingleton<UserDataService>();
                    _=services.AddSingleton<CommandDataService>();
                    _=services.AddSingleton<SessionDataService>();
                    _=services.AddSingleton<MessageTrackingDataService>();
                    _=services.AddSingleton<DatabaseInitializerService>();

                    _=services.AddOptions<WorkerOptions>()
                        .Bind(context.Configuration.GetSection(WorkerOptions.SectionName))
                        .Validate(options => options.ProcessTimeoutMinutes > 0, "Worker:ProcessTimeoutMinutes must be greater than 0")
                        .Validate(options => options.MaxRetries >= 0, "Worker:MaxRetries must be greater than or equal to 0")
                        .Validate(options => options.RetryDelayBaseSeconds > 0, "Worker:RetryDelayBaseSeconds must be greater than 0")
                        .Validate(options => options.FallbackPollingIntervalSeconds > 0, "Worker:FallbackPollingIntervalSeconds must be greater than 0")
                        .Validate(options => options.Partitions.Count > 0, "Worker:Partitions must contain at least one partition")
                        .Validate(options => options.Partitions.All(p => p.Key >= 0 && p.Value > 0), "Worker:Partitions thresholds must be non-negative and pool sizes must be greater than 0")
                        .Validate(options => options.Commands.Count > 0, "Worker:Commands must contain at least one command")
                        .Validate(options => options.Commands.All(c => !string.IsNullOrWhiteSpace(c.Value.ExecutablePath)), "Worker:Commands executable paths are required")
                        .Validate(options => options.Commands.All(c => !string.IsNullOrWhiteSpace(c.Value.ArgumentsTemplate)), "Worker:Commands argument templates are required")
                        .ValidateOnStart();
                    _=services.Configure<BimIntegrationOptions>(context.Configuration.GetSection(BimIntegrationOptions.SectionName));
                    _=services.Configure<DialogDismisserOptions>(context.Configuration.GetSection(DialogDismisserOptions.SectionName));

                    // BIM-интеграция (Revit + Navisworks)
                    _=services.AddSingleton<RevitVersionDetector>();
                    _=services.AddSingleton<RevitPathResolver>();
                    _=services.AddSingleton<RevitProcessTracker>();
                    _=services.AddSingleton<DialogDismisser>();
                    _=services.AddSingleton<NavisworksPathResolver>();
                    _=services.AddSingleton<NavisworksProcessTracker>();

                    // Компоненты выполнения команд (декомпозиция CommandExecutionService)
                    _=services.AddSingleton<PartitionPoolManager>();
                    _=services.AddSingleton<CommandPreparer>();
                    _=services.AddSingleton<SessionCompletionTracker>();
                    _=services.AddSingleton<ProcessRunner>();

                    _=services.AddHostedService<CommandExecutionService>();
                    _=services.AddHostedService<SessionCleanupService>();

                    // Health check HTTP-сервер
                    _=services.AddOptions<HealthCheckOptions>()
                        .Bind(context.Configuration.GetSection(HealthCheckOptions.SectionName))
                        .Validate(options => options.Port is >0 and <=65535, "Port must be between 1 and 65535");

                    var connectionString = context.Configuration.GetConnectionString("Postgres") ?? DataAccessBase.DefaultConnectionString;
                    _=services.AddHostedService(sp =>
                    {
                        var options = sp.GetRequiredService<IOptions<HealthCheckOptions>>();
                        var logger = sp.GetRequiredService<ILogger<HealthCheckHostedService>>();
                        var bimOptions = sp.GetRequiredService<IOptions<BimIntegrationOptions>>().Value;
                        var processRunner = sp.GetRequiredService<ProcessRunner>();
                        var svc = HealthCheckServiceFactory.Create(options, logger, connectionString);

                        svc.AdditionalChecks["bimInstallRoot"] = _ =>
                            Task.FromResult(new HealthComponentStatus
                            {
                                Status = Directory.Exists(bimOptions.RevitInstallRoot) ? "healthy" : "unhealthy",
                                Message = Directory.Exists(bimOptions.RevitInstallRoot)
                                    ? null
                                    : $"Directory not found: {bimOptions.RevitInstallRoot}",
                            });

                        svc.AdditionalChecks["activeProcesses"] = _ =>
                            Task.FromResult(new HealthComponentStatus
                            {
                                Status = "healthy",
                                Message = $"active={processRunner.ActiveProcesses.Count()}",
                            });

                        return svc;
                    });
                })
                .UseSerilog((context, services, loggerConfiguration) =>
                    {
                        SerilogSetup.ConfigureFileLogging(context.Configuration, services, loggerConfiguration, "Worker");

                        // Отдельный файл для BIM-специфичных логов (Revit, Navisworks — TelegramBot.Worker.BimLib.*)
                        var logBasePath = context.Configuration
                            .GetSection(FileSystemOptions.SectionName)[nameof(FileSystemOptions.LogDirectory)];
                        if (string.IsNullOrWhiteSpace(logBasePath))
                        {
                            logBasePath = null;
                        }

                        _=loggerConfiguration.WriteTo.Logger(lc => lc
                            .MinimumLevel.Information()
                            .Enrich.FromLogContext()
                            .Filter.ByIncludingOnly(BimLibLogFilter.IsBimLibEvent)
                            .WriteTo.File(
                                SerilogSetup.GetLogPath(Path.Combine("Worker", "BimLib"), logBasePath),
                                rollingInterval: RollingInterval.Day));
                    })
                .Build();

            // Initialize WinApiHelper logger for safe P/Invoke error logging
            var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
            WinApiHelper.SetLogger(loggerFactory.CreateLogger("TelegramBot.Worker.BimLib.Native.WinApiHelper"));

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
