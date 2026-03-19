using Telegram.Bot;
using TelegramBot.Core.Config;
using TelegramBot.Core.Interfaces;
using TelegramBot.Data;
using TelegramBot.Server.Interfaces;
using TelegramBot.Server.Services.Application;
using TelegramBot.Server.Services.Application.Handlers;
using TelegramBot.Server.Services.Application.Sessions;
using TelegramBot.Server.Services.Infrastructure.FileSystem;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Extensions;

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
        _ = services.AddSingleton<ICallbackHandler, FileNavigationHandler>();
        _ = services.AddSingleton<ICallbackHandler, FileSelectionHandler>();
        _ = services.AddSingleton<ICallbackHandler, ExportCommandHandler>();
        _ = services.AddSingleton<ICallbackHandler, AutomationCommandHandler>();
        _ = services.AddSingleton<ICallbackHandler, SessionManagementHandler>();
        _ = services.AddSingleton<ICallbackHandler, CommandSelectionHandler>();
        _ = services.AddSingleton<ICallbackDispatcher, CallbackDispatcher>();

        return services;
    }

    private static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        _ = services.AddSingleton<ICommandAppService, CommandAppService>();
        _ = services.AddSingleton<ISessionManager>(_ => new SessionManager(TimeSpan.FromMinutes(5)));

        return services;
    }

    private static IServiceCollection AddInfrastructureServices(this IServiceCollection services)
    {
        _ = services.AddSingleton<IDataService, SqliteDataService>();
        _ = services.AddSingleton<IFileSystemBrowser, FileSystemBrowser>();

        return services;
    }

    private static IServiceCollection AddTelegramServices(this IServiceCollection services, IConfiguration configuration)
    {
        _ = services.AddSingleton<ITelegramBotClient>(_ =>
        {
            var token = configuration["TelegramBot:Token"]
                ?? throw new InvalidOperationException(
                    "TelegramBot:Token is not configured. " +
                    "Set it in appsettings.Local.json or via environment variable TelegramBot__Token.");
            return new TelegramBotClient(token);
        });

        _ = services.AddSingleton<ITelegramOutputService, TelegramOutputService>();
        _ = services.AddSingleton<ITelegramUpdateMapper, TelegramUpdateMapper>();
        _ = services.AddSingleton<IKeyboardBuilder, KeyboardBuilder>();
        _ = services.AddHostedService<TelegramBotHostedService>();

        return services;
    }
}
