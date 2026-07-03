using Microsoft.Extensions.Options;
using System.Threading.Channels;
using Telegram.Bot;
using TelegramBot.Core.Config;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Services;
using TelegramBot.Data;
using TelegramBot.Server.Interfaces;
using TelegramBot.Server.Middleware;
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
        _=services.AddOptions<FileSystemOptions>()
            .Bind(configuration.GetSection(FileSystemOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.RootPath), "RootPath is required")
            .Validate(options => Directory.Exists(options.RootPath), "RootPath directory must exist");

        _=services.AddOptions<BotOptions>()
            .Bind(configuration.GetSection(BotOptions.SectionName))
            .ValidateOnStart()
            .Validate(options => !string.IsNullOrWhiteSpace(options.Token), "TelegramBot:Token is required");

        _=services.AddOptions<RateLimitOptions>()
            .Bind(configuration.GetSection(RateLimitOptions.SectionName));

        return services;
    }

    private static IServiceCollection AddCallbackHandlers(this IServiceCollection services)
    {
        _=services.AddSingleton<ICallbackHandler, AccessRequestHandler>();
        _=services.AddSingleton<ICallbackHandler, FileNavigationHandler>();
        _=services.AddSingleton<ICallbackHandler, FileSelectionHandler>();
        _=services.AddSingleton<ICallbackHandler, CommandToggleHandler>();
        _=services.AddSingleton<ICallbackHandler, SessionManagementHandler>();
        _=services.AddSingleton<ICallbackHandler, CommandSelectionHandler>();
        _=services.AddSingleton<CallbackDispatcher>();

        return services;
    }

    private static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        _=services.AddSingleton<CommandAppService>();
        _=services.AddSingleton<AuthorizationMiddleware>();
        _=services.AddSingleton<RateLimiter>();
        _=services.AddSingleton<SlashCommandService>();
        _=services.AddSingleton<SessionsListRenderer>();
        _=services.AddSingleton<SessionManager>(_ => new SessionManager(TimeSpan.FromMinutes(5)));
        _=services.AddSingleton<MessageTrackingService>();

        return services;
    }

    private static IServiceCollection AddInfrastructureServices(this IServiceCollection services)
    {
        _=services.AddSingleton<UserDataService>();
        _=services.AddSingleton<CommandDataService>();
        _=services.AddSingleton<SessionDataService>();
        _=services.AddSingleton<MessageTrackingDataService>();
        _=services.AddSingleton<NotificationOutboxDataService>();
        _=services.AddSingleton<DataServices>();
        _=services.AddSingleton<DatabaseInitializerService>();
        _=services.AddSingleton<FileSystemBrowser>();

        return services;
    }

    private static IServiceCollection AddTelegramServices(this IServiceCollection services)
    {
        _=services.AddSingleton<ITelegramBotClient>(serviceProvider =>
        {
            var botOptions = serviceProvider.GetRequiredService<IOptions<BotOptions>>().Value;

            return string.IsNullOrWhiteSpace(botOptions.Token)
                ? throw new InvalidOperationException("TelegramBot:Token is not configured.")
                : (ITelegramBotClient)new TelegramBotClient(botOptions.Token);
        });

        _=services.AddSingleton<ITelegramOutputService>(sp =>
        {
            var botClient = sp.GetRequiredService<ITelegramBotClient>();
            var messageTrackingService = sp.GetRequiredService<MessageTrackingDataService>();
            var logger = sp.GetRequiredService<ILogger<TelegramOutputService>>();

            return new TelegramOutputService(botClient, messageTrackingService, logger);
        });
        _=services.AddSingleton<TelegramUpdateMapper>();
        _=services.AddSingleton<KeyboardBuilder>();
        _=services.AddSingleton(_ => Channel.CreateBounded<NotificationItem>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        }));
        _=services.AddHostedService<TelegramBotHostedService>();
        _=services.AddHostedService<CommandNotificationService>();
        _=services.AddHostedService<NotificationSenderService>();

        return services;
    }
}
