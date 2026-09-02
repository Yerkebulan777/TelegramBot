using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Serilog;
using System.Runtime.Versioning;
using System.Text;
using TelegramBot.Core.Config;
using TelegramBot.Core.Helpers;
using TelegramBot.Data;
using TelegramBot.BimLib.Config;
using TelegramBot.BimLib.Helpers;
using TelegramBot.BimLib.Monitor;
using TelegramBot.BimLib.Services;
using TelegramBot.Worker.Services;

[assembly: SupportedOSPlatform("windows")]

namespace TelegramBot.Worker;

public static class Program
{
    public static async Task Main(string[]? args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        SerilogSetup.ConfigureBootstrapLogger("Worker");

        try
        {
            using var host = Host.CreateDefaultBuilder(args)

                .UseWindowsService(options => options.ServiceName = "TelegramBotWorker")

                .ConfigureAppConfiguration((context, config) =>
                {
                    _=config.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
                    _=config.AddEnvironmentVariables();
                })
                .ConfigureServices((context, services) =>
                {
                    _=services.AddSingleton<CommandDataService>();
                    _=services.AddSingleton<SessionDataService>();
                    _=services.AddOptions<WorkerOptions>()
                        .Bind(context.Configuration.GetSection(WorkerOptions.SectionName))
                        .Validate(options => options.ProcessTimeoutMinutes > 0, "Worker:ProcessTimeoutMinutes must be greater than 0")
                        .Validate(options => options.MaxRetries >= 0, "Worker:MaxRetries must be greater than or equal to 0")
                        .Validate(options => options.RetryDelayBaseSeconds > 0, "Worker:RetryDelayBaseSeconds must be greater than 0")
                        .Validate(options => options.FallbackPollingIntervalSeconds >= 0, "Worker:FallbackPollingIntervalSeconds must be greater than or equal to 0")
                        .Validate(options => options.CleanupIntervalSeconds >= 0, "Worker:CleanupIntervalSeconds must be greater than or equal to 0")
                        .Validate(options => options.ProcessMonitorIntervalSeconds >= 0, "Worker:ProcessMonitorIntervalSeconds must be greater than or equal to 0")
                        .Validate(options => options.UnresponsiveThresholdSeconds > 0, "Worker:UnresponsiveThresholdSeconds must be greater than 0")
                        .Validate(options => options.MaxConcurrentCommands > 0, "Worker:MaxConcurrentCommands must be greater than 0")
                        .Validate(options => options.Commands.Count > 0, "Worker:Commands must contain at least one command")
                        .Validate(options => options.Commands.All(c => !string.IsNullOrWhiteSpace(c.Value.ExecutablePath)), "Worker:Commands executable paths are required")
                        .ValidateOnStart();

                    _=services.AddOptions<BimIntegrationOptions>()
                        .Bind(context.Configuration.GetSection(BimIntegrationOptions.SectionName))
                        .Validate(options => options.MinSupportedVersion > 0, "BimIntegration:MinSupportedVersion must be greater than 0")
                        .Validate(options => options.MaxSupportedVersion >= options.MinSupportedVersion, "BimIntegration:MaxSupportedVersion must be greater than or equal to MinSupportedVersion")
                        .ValidateOnStart();

                    _=services.AddOptions<DialogDismisserOptions>()
                        .Bind(context.Configuration.GetSection(DialogDismisserOptions.SectionName))
                        .Validate(options => options.MaxDismissAttempts >= 0, "DialogDismisser:MaxDismissAttempts must be greater than or equal to 0")
                        .Validate(options => options.CloseButtonTexts is { Length: > 0 } buttonTexts && buttonTexts.All(text => !string.IsNullOrWhiteSpace(text)), "DialogDismisser:CloseButtonTexts must contain non-empty values")
                        .Validate(options => options.ExclusionDialogTitles?.All(title => !string.IsNullOrWhiteSpace(title)) ?? false, "DialogDismisser:ExclusionDialogTitles must contain non-empty values")
                        .ValidateOnStart();

                    _=services.Configure<FileSystemOptions>(context.Configuration.GetSection(FileSystemOptions.SectionName));

                    // BIM-интеграция (Revit + Navisworks)
                    _=services.AddSingleton<RevitVersionDetector>();
                    _=services.AddSingleton<NavisworksPathResolver>();
                    _=services.AddSingleton<RevitPathResolver>();
                    _=services.AddSingleton<DialogDismisser>();

                    _=services.AddSingleton<CommandPreparer>();
                    _=services.AddSingleton<ProcessStarter>();
                    _=services.AddSingleton<OutputCollector>();
                    _=services.AddSingleton<ResultAnalyzer>();
                    _=services.AddSingleton<ProcessRunner>();
                    _=services.AddSingleton<CommandOrchestrator>();

                    _=services.AddHostedService<CommandExecutionService>();
                    _=services.AddHostedService<SessionCleanupService>();
                })

                .UseSerilog((context, services, loggerConfiguration) =>
                {
                    SerilogSetup.ConfigureFileLogging(context.Configuration, services, loggerConfiguration, "Worker");

                    // Отдельный файл для BIM-специфичных логов (Revit, Navisworks — TelegramBot.BimLib.*)
                    var logBasePath = SerilogSetup.GetConfiguredLogBasePath(context.Configuration);
                    _ = loggerConfiguration.WriteTo.Logger(lc => lc
                        .MinimumLevel.Information()
                        .Enrich.FromLogContext()
                        .WriteToRollingFile("BimLib", logBasePath));
                })
                .Build();

            // Initialize WinApiHelper logger for safe P/Invoke error logging
            var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
            WinApiHelper.SetLogger(loggerFactory.CreateLogger("TelegramBot.BimLib.Native.WinApiHelper"));

            // Гарантируем, что TaskDirectory существует — иначе первая же команда упадёт при записи task_*.xml.
            // Путь настраивается через FileSystem:TaskDirectory; по умолчанию %USERPROFILE%\Documents\TelegramBot\TaskDirectory.
            var fileSystemOptions = host.Services.GetRequiredService<IOptions<FileSystemOptions>>().Value;
            var taskDir = fileSystemOptions.GetEffectiveTaskDirectory();

            try
            {
                _=Directory.CreateDirectory(taskDir);
                Log.Information("TaskDirectory ready: {Path}", taskDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var message = $"Failed to create TaskDirectory '{taskDir}'";
                throw new InvalidOperationException(message, ex);
            }

            await host.RunAsync();
        }
        catch (Exception ex)
        {
            // Ненулевой exit code обязателен: иначе супервизор (NSSM/sc/Task Scheduler) сочтёт процесс
            // завершённым штатно и не перезапустит. Сериложный Fatal сам по себе exit code не выставляет.
            Environment.ExitCode = 1;
            Log.Fatal(ex, "Worker terminated unexpectedly");
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }
}
