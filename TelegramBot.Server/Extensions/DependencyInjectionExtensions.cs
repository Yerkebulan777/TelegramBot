using Microsoft.Extensions.Options;
using System.Threading.Channels;
using Telegram.Bot;
using TelegramBot.Core.Config;
using TelegramBot.Core.Helpers;
using TelegramBot.Core.Interfaces;
using TelegramBot.Data;
using TelegramBot.Server.Models;
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
            .Bind(configuration.GetSection(RateLimitOptions.SectionName))
            .Validate(options => options.MaxRequests > 0, "RateLimit:MaxRequests must be greater than 0")
            .Validate(options => options.WindowSeconds > 0, "RateLimit:WindowSeconds must be greater than 0")
            .Validate(options => options.MaxFilesPerUserPerDay >= 0, "RateLimit:MaxFilesPerUserPerDay must be greater than or equal to 0")
            .ValidateOnStart();

        return services;
    }

    private static IServiceCollection AddCallbackHandlers(this IServiceCollection services)
    {
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
        _=services.AddSingleton<RateLimiter>();
        _=services.AddSingleton<SlashCommandService>();
        _=services.AddSingleton<SessionsListRenderer>();
        _=services.AddSingleton<MessageTrackingService>();
        _=services.AddSingleton<FileActionsKeyboardService>();
        _=services.AddSingleton<SessionManager>(_ => new SessionManager(TimeSpan.FromMinutes(5)));
        return services;
    }

    private static IServiceCollection AddInfrastructureServices(this IServiceCollection services)
    {
        _=services.AddSingleton<CommandDataService>();
        _=services.AddSingleton<SessionDataService>();
        _=services.AddSingleton<MessageTrackingDataService>();
        _=services.AddSingleton<NotificationOutboxDataService>();
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

        _=services.AddSingleton<TelegramOutputService>();
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
