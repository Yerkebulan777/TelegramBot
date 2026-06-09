using Microsoft.Extensions.Configuration;
using Serilog;

namespace TelegramBot.Worker.Helpers;

/// <summary>
/// Shared Serilog configuration helpers for Worker entry point.
/// </summary>
public static class SerilogSetup
{
    /// <summary>Возвращает путь к файлу лога для указанного подраздела (Server / Worker).</summary>
    public static string GetLogPath(string subfolder)
    {
        var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(documentsPath, "TelegramBot", "Logs", subfolder, "log-.txt");
    }

    /// <summary>Создаёт bootstrap-логгер с выводом в консоль и файл.</summary>
    public static void ConfigureBootstrapLogger(string subfolder)
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File(GetLogPath(subfolder), rollingInterval: RollingInterval.Day)
            .CreateBootstrapLogger();
    }

    /// <summary>
    /// Настраивает Serilog для хост-приложения: читает конфигурацию,
    /// обогащает из DI, пишет в файл с дневной ротацией.
    /// Подходит для использования внутри .UseSerilog().
    /// </summary>
    public static void ConfigureFileLogging(
        IConfiguration configuration,
        IServiceProvider services,
        LoggerConfiguration loggerConfiguration,
        string subfolder)
    {
        _=loggerConfiguration
            .ReadFrom.Configuration(configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.File(GetLogPath(subfolder), rollingInterval: RollingInterval.Day);
    }
}
