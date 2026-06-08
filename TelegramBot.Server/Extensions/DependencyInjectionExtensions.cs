using Microsoft.Extensions.Options;
using Telegram.Bot;
using TelegramBot.Core.Config;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Services;
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
        return services
           .AddCallbackHandlers()
           .AddApplicationServices()
           .AddInfrastructureServices()
           .AddConfiguration(configuration)
           .AddTelegramServices();
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

        services.AddOptions<RateLimitOptions>()
            .Bind(configuration.GetSection(RateLimitOptions.SectionName));

        return services;
    }

    private static IServiceCollection AddCallbackHandlers(this IServiceCollection services)
    {
        services.AddSingleton<ICallbackHandler, AccessRequestHandler>();
        services.AddSingleton<ICallbackHandler, FileNavigationHandler>();
        services.AddSingleton<ICallbackHandler, FileSelectionHandler>();
        services.AddSingleton<ICallbackHandler, CommandToggleHandler>();
        services.AddSingleton<ICallbackHandler, SessionManagementHandler>();
        services.AddSingleton<ICallbackHandler, CommandSelectionHandler>();
        services.AddSingleton<ICallbackDispatcher, CallbackDispatcher>();

        return services;
    }

    private static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddSingleton<ICommandAppService, CommandAppService>();
        services.AddSingleton<RateLimiter>();
        services.AddSingleton<ISlashCommandService, SlashCommandService>();
        services.AddSingleton<ISessionManager>(_ => new SessionManager(TimeSpan.FromMinutes(5)));

        return services;
    }

    private static IServiceCollection AddInfrastructureServices(this IServiceCollection services)
    {
        services.AddSingleton<IDataService, PostgresDataService>();
        services.AddSingleton<FileSystemBrowser>();

        return services;
    }

    private static IServiceCollection AddTelegramServices(this IServiceCollection services)
    {
        services.AddSingleton<ITelegramBotClient>(serviceProvider =>
        {
            var botOptions = serviceProvider.GetRequiredService<IOptions<BotOptions>>().Value;

            return string.IsNullOrWhiteSpace(botOptions.Token)
                ? throw new InvalidOperationException("TelegramBot:Token is not configured.")
                : (ITelegramBotClient)new TelegramBotClient(botOptions.Token);
        });

        services.AddSingleton<ITelegramOutputService>(sp =>
        {
            var botClient = sp.GetRequiredService<ITelegramBotClient>();
            var logger = sp.GetRequiredService<ILogger<TelegramOutputService>>();
            var botOptions = sp.GetRequiredService<IOptions<BotOptions>>().Value;
            var adminId = botOptions.AdminUserIds?.FirstOrDefault();

            return new TelegramOutputService(botClient, logger, adminId);
        });
        services.AddSingleton<TelegramUpdateMapper>();
        services.AddSingleton<IKeyboardBuilder, KeyboardBuilder>();
        services.AddHostedService<TelegramBotHostedService>();
        services.AddHostedService<CommandNotificationService>();

        return services;
    }
}
