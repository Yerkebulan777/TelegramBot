using Microsoft.Extensions.Options;
using Telegram.Bot;
using TelegramBot.Core.Config;
using TelegramBot.Core.Helpers;
using TelegramBot.Data;
using TelegramBot.Server.Services.Application;
using TelegramBot.Server.Services.Application.Handlers;
using TelegramBot.Server.Services.Infrastructure.FileSystem;
using TelegramBot.Server.Services.Infrastructure.Status;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Extensions;

public static class DependencyInjectionExtensions
{
    public static IServiceCollection AddTelegramBotServer(this IServiceCollection services, IConfiguration configuration)
    {
        _ = services.AddOptions<FileSystemOptions>()
            .Bind(configuration.GetSection(FileSystemOptions.SectionName))
            .ValidateOnStart();

        _ = services.AddOptions<BotOptions>()
            .Bind(configuration.GetSection(BotOptions.SectionName))
            .ValidateOnStart()
            .Validate(options => !string.IsNullOrWhiteSpace(options.Token), "TelegramBot:Token is required");

        _ = services.AddOptions<RateLimitOptions>()
            .Bind(configuration.GetSection(RateLimitOptions.SectionName))
            .Validate(options => options.MaxRequests > 0, "RateLimit:MaxRequests must be greater than 0")
            .Validate(options => options.WindowSeconds > 0, "RateLimit:WindowSeconds must be greater than 0")
            .Validate(options => options.MaxFilesPerUserPerDay >= 0, "RateLimit:MaxFilesPerUserPerDay must be greater than or equal to 0")
            .ValidateOnStart();

        _ = services.AddOptions<MessageCleanupOptions>()
            .Bind(configuration.GetSection(MessageCleanupOptions.SectionName))
            .Validate(options => options.IntervalMinutes > 0, "MessageCleanup:IntervalMinutes must be greater than 0")
            .Validate(options => options.RetentionHours > 0, "MessageCleanup:RetentionHours must be greater than 0")
            .Validate(options => options.TemporaryRetentionMinutes > 0 && options.TemporaryRetentionMinutes < options.MaximumDeletionAgeHours * 60,
                "MessageCleanup:TemporaryRetentionMinutes must be positive and below the deletion age limit")
            .Validate(options => options.MaximumDeletionAgeHours is > 0 and < 48, "MessageCleanup:MaximumDeletionAgeHours must be between 1 and 47")
            .Validate(options => options.MaximumDeletionAgeHours > options.RetentionHours, "MessageCleanup:MaximumDeletionAgeHours must be greater than RetentionHours")
            .Validate(options => options.BatchSize > 0, "MessageCleanup:BatchSize must be greater than 0")
            .ValidateOnStart();

        _ = services.AddSingleton<CallbackHandlerBase, FileSelectionHandler>();
        _ = services.AddSingleton<CallbackHandlerBase, CommandToggleHandler>();
        _ = services.AddSingleton<CallbackHandlerBase, SessionManagementHandler>();
        _ = services.AddSingleton<CallbackHandlerBase, CommandSelectionHandler>();
        _ = services.AddSingleton<CallbackHandlerBase, RootPathHandler>();
        _ = services.AddSingleton<CallbackDispatcher>();

        _ = services.AddSingleton<CommandAppService>();
        _ = services.AddSingleton<RateLimiter>();
        _ = services.AddSingleton<SlashCommandService>();
        _ = services.AddSingleton<SessionsListRenderer>();
        _ = services.AddSingleton<MessageTrackingService>();
        _ = services.AddSingleton<SessionManager>(_ => new SessionManager(TimeSpan.FromMinutes(5)));

        _ = services.AddSingleton<SchemaReadyGate>();
        _ = services.AddSingleton<CommandDataService>();
        _ = services.AddSingleton<UncRootPathValidator>();
        _ = services.AddSingleton<RootPathDataService>();
        _ = services.AddSingleton<RootPathProvider>();
        _ = services.AddSingleton<SessionDataService>();
        _ = services.AddSingleton<MessageTrackingDataService>();
        _ = services.AddSingleton<NotificationOutboxDataService>();
        _ = services.AddSingleton<FileSystemBrowser>();

        _ = services.AddSingleton<ITelegramBotClient>(serviceProvider =>
        {
            var botOptions = serviceProvider.GetRequiredService<IOptions<BotOptions>>().Value;

            return string.IsNullOrWhiteSpace(botOptions.Token)
                ? throw new InvalidOperationException("TelegramBot:Token is not configured.")
                : (ITelegramBotClient)new TelegramBotClient(botOptions.Token);
        });

        _ = services.AddSingleton<TelegramOutputService>();
        _ = services.AddSingleton<KeyboardBuilder>();
        _ = services.AddSingleton<ServerHealthMonitor>();
        _ = services.AddSingleton<ServerHealthCheckService>();
        _ = services.AddHostedService<ServerTrayHostedService>();
        _ = services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<ServerHealthCheckService>());
        _ = services.AddHostedService<DatabaseInitializerService>();
        _ = services.AddHostedService<TelegramBotHostedService>();
        _ = services.AddHostedService<NotificationSenderService>();
        _ = services.AddHostedService<TrackedMessageCleanupService>();

        return services;
    }
}
