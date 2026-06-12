using Microsoft.Extensions.Configuration;
using Serilog;
using TelegramBot.Core.Config;

namespace TelegramBot.Core.Helpers;

/// <summary>
/// Shared Serilog configuration helpers for Server and Worker entry points.
/// Consolidated from TelegramBot.Server.Helpers and TelegramBot.Worker.Helpers to eliminate duplication.
/// </summary>
public static class SerilogSetup
{
    /// <summary>Дефолтный базовый путь для логов: %USERPROFILE%\Documents\TelegramBot\Logs.</summary>
    private static string DefaultLogBasePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "TelegramBot", "Logs");

    /// <summary>Возвращает путь к файлу лога для указанного подраздела (Server / Worker).</summary>
    public static string GetLogPath(string subfolder, string? logBasePath = null)
    {
        logBasePath ??= DefaultLogBasePath;
        return Path.Combine(logBasePath, subfolder, "log-.txt");
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
    /// Путь к логам читается из FileSystem:LogDirectory (appsettings.json),
    /// если не задан — используется дефолтный %USERPROFILE%\Documents\TelegramBot\Logs.
    /// </summary>
    public static void ConfigureFileLogging(
        IConfiguration configuration,
        IServiceProvider services,
        LoggerConfiguration loggerConfiguration,
        string subfolder)
    {
        var logBasePath = configuration.GetSection(FileSystemOptions.SectionName)[nameof(FileSystemOptions.LogDirectory)];
        if (string.IsNullOrWhiteSpace(logBasePath))
        {
            logBasePath = null;
        }

        _=loggerConfiguration
            .ReadFrom.Configuration(configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.File(GetLogPath(subfolder, logBasePath), rollingInterval: RollingInterval.Day);
    }
}
