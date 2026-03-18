using Telegram.Bot;
using TelegramBotServer.Config;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Services;
using TelegramBotServer.Services.Application;
using TelegramBotServer.Services.Application.Handlers;
using TelegramBotServer.Services.Infrastructure.Telegram;

namespace TelegramBotServer.Extensions;

public static class DependencyInjectionExtensions
{
    /// <summary>Регистрирует все сервисы бота в DI-контейнере.</summary>
    public static IServiceCollection AddTelegramBotServer(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddCallbackHandlers()
            .AddApplicationServices()
            .AddInfrastructureServices()
            .AddConfiguration(configuration)
            .AddTelegramServices(configuration);

        return services;
    }

    private static IServiceCollection AddConfiguration(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<FileSystemOptions>()
            .Bind(configuration.GetSection(FileSystemOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.RootPath), "RootPath is required")
            .Validate(options => Directory.Exists(options.RootPath), "RootPath directory must exist");

        return services;
    }

    private static IServiceCollection AddCallbackHandlers(this IServiceCollection services)
    {
        services
            .AddSingleton<ICallbackHandler, FileNavigationHandler>()
            .AddSingleton<ICallbackHandler, FileSelectionHandler>()
            .AddSingleton<ICallbackHandler, ExportCommandHandler>()
            .AddSingleton<ICallbackHandler, AutomationCommandHandler>()
            .AddSingleton<ICallbackHandler, SessionManagementHandler>()
            .AddSingleton<ICallbackHandler, CommandSelectionHandler>()
            .AddSingleton<CallbackDispatcher>();

        return services;
    }

    private static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services
            .AddSingleton<ICommandAppService, CommandAppService>()
            .AddSingleton<ISessionManager>(_ => new SessionManager(TimeSpan.FromMinutes(5)));

        return services;
    }

    private static IServiceCollection AddInfrastructureServices(this IServiceCollection services)
    {
        services
            .AddSingleton<IDataService, SqliteDataService>()
            .AddSingleton<IFileSystemBrowser, FileSystemBrowser>();

        return services;
    }

    private static IServiceCollection AddTelegramServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ITelegramBotClient>(_ =>
        {
            var token = configuration["TelegramBot:Token"]
                ?? throw new InvalidOperationException(
                    "TelegramBot:Token is not configured. " +
                    "Set it in appsettings.Local.json or via environment variable TelegramBot__Token.");
            return new TelegramBotClient(token);
        });

        services
            .AddSingleton<ITelegramOutputService, TelegramOutputService>()
            .AddSingleton<ITelegramUpdateMapper, TelegramUpdateMapper>()
            .AddSingleton<IKeyboardBuilder, KeyboardBuilder>()
            .AddHostedService<TelegramBotHostedService>();

        return services;
    }
}

