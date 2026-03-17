using Telegram.Bot;
using TelegramBotServer.Config;
using TelegramBotServer.Services;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Services.Application;
using TelegramBotServer.Services.Infrastructure.Telegram;

namespace TelegramBotServer.Extensions;

public static class DependencyInjectionExtensions
{
    public static IServiceCollection AddTelegramBotServer(this IServiceCollection services, IConfiguration configuration)
    {
        _ = services
            .AddConfiguration(configuration)
            .AddCallbackHandlers()
            .AddApplicationServices()
            .AddInfrastructureServices()
            .AddTelegramServices(configuration);

        return services;
    }

    private static IServiceCollection AddConfiguration(this IServiceCollection services, IConfiguration configuration)
    {
        // Register FileSystemOptions as IOptions for dependency injection
        _ = services.AddOptions<FileSystemOptions>()
            .Bind(configuration.GetSection(FileSystemOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.RootPath), "RootPath is required")
            .Validate(options => Directory.Exists(options.RootPath), "RootPath directory must exist");

        return services;
    }

    private static IServiceCollection AddCallbackHandlers(this IServiceCollection services)
    {
        // Register all callback handlers (order doesn't matter - dispatcher sorts by priority)
        _ = services.AddSingleton<ICallbackHandler, Services.Application.Handlers.FileNavigationHandler>();
        _ = services.AddSingleton<ICallbackHandler, Services.Application.Handlers.FileSelectionHandler>();
        _ = services.AddSingleton<ICallbackHandler, Services.Application.Handlers.ExportCommandHandler>();
        _ = services.AddSingleton<ICallbackHandler, Services.Application.Handlers.AutomationCommandHandler>();
        _ = services.AddSingleton<ICallbackHandler, Services.Application.Handlers.SessionManagementHandler>();
        _ = services.AddSingleton<ICallbackHandler, Services.Application.Handlers.CommandSelectionHandler>();

        // Register the dispatcher
        _ = services.AddSingleton<CallbackDispatcher>();

        return services;
    }

    private static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        _ = services.AddSingleton<IAuthService, AuthService>();
        _ = services.AddSingleton<ICommandAppService, CommandAppService>();

        // Register Command Handlers
        _ = services.AddSingleton<IUserCommandHandler, Services.Application.Handlers.Commands.StartCommandHandler>();
        _ = services.AddSingleton<IUserCommandHandler, Services.Application.Handlers.Commands.ExportCommandHandler>();
        _ = services.AddSingleton<IUserCommandHandler, Services.Application.Handlers.Commands.StatusCommandHandler>();
        _ = services.AddSingleton<IUserCommandHandler, Services.Application.Handlers.Commands.AutomationCommandHandler>();
        _ = services.AddSingleton<IUserCommandHandler, Services.Application.Handlers.Commands.HelpCommandHandler>();
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
            string token = configuration["TelegramBot:Token"]
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

