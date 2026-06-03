using Microsoft.Extensions.Options;
using Telegram.Bot;
using TelegramBot.Core.Config;
using TelegramBot.Core.Interfaces;
using TelegramBot.Data;
using TelegramBot.Server.Interfaces;
using TelegramBot.Server.Services.Application;
using TelegramBot.Server.Services.Application.Handlers;
using TelegramBot.Server.Services.Infrastructure.FileSystem;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Extensions;

public static class DependencyInjectionExtensions
{
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

        services.AddOptions<BotOptions>()
            .Bind(configuration.GetSection(BotOptions.SectionName))
            .ValidateOnStart()
            .Validate(options => !string.IsNullOrWhiteSpace(options.Token), "TelegramBot:Token is required");

        return services;
    }

    private static IServiceCollection AddCallbackHandlers(this IServiceCollection services)
    {
        _ = services.AddSingleton<ICallbackHandler, AccessRequestHandler>();
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
        _ = services.AddSingleton<ISlashCommandService, SlashCommandService>();
        _ = services.AddSingleton<ISessionManager>(sp =>
            new SessionManager(TimeSpan.FromMinutes(5), sp.GetRequiredService<IDataService>()));

        return services;
    }

    private static IServiceCollection AddInfrastructureServices(this IServiceCollection services)
    {
        _ = services.AddSingleton<IDataService, PostgresDataService>();
        _ = services.AddSingleton<IFileSystemBrowser, FileSystemBrowser>();

        return services;
    }

    private static IServiceCollection AddTelegramServices(this IServiceCollection services, IConfiguration configuration)
    {
        _ = services.AddSingleton<ITelegramBotClient>(serviceProvider =>
        {
            var botOptions = serviceProvider.GetRequiredService<IOptions<BotOptions>>().Value;

            if (string.IsNullOrWhiteSpace(botOptions.Token))
            {
                throw new InvalidOperationException(
                    "TelegramBot:Token is not configured. " +
                    "Set it in appsettings.Local.json or via environment variable TELEGRAM_BOT_TOKEN.");
            }

            return new TelegramBotClient(botOptions.Token);
        });

        _ = services.AddSingleton<ITelegramOutputService, TelegramOutputService>();
        _ = services.AddSingleton<ITelegramUpdateMapper, TelegramUpdateMapper>();
        _ = services.AddSingleton<IKeyboardBuilder, KeyboardBuilder>();
        _ = services.AddHostedService<TelegramBotHostedService>();

        return services;
    }
}
