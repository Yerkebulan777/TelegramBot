using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;

namespace TelegramBot.Data;

/// <summary>
/// Extension methods to initialize the database at application startup.
/// </summary>
public static class DatabaseInitializer
{
    /// <summary>
    /// Инициализирует базу данных при запуске приложения.
    /// </summary>
    public static async Task InitializeDatabaseAsync(this IHost host)
    {
        using IServiceScope scope = host.Services.CreateScope();
        IDataService dataService = scope.ServiceProvider.GetRequiredService<IDataService>();
        await dataService.InitializeDatabaseAsync();
    }

    /// <summary>
    /// Гарантирует, что сконфигурированные администраторы присутствуют в БД со статусом Approved+Admin.
    /// </summary>
    public static async Task SeedAdminUsersAsync(this IHost host)
    {
        using IServiceScope scope = host.Services.CreateScope();
        var dataService = scope.ServiceProvider.GetRequiredService<IDataService>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var adminIds = configuration.GetSection("TelegramBot:AdminUserIds").Get<long[]>() ?? [];
        var now = DateTime.UtcNow;

        foreach (var adminId in adminIds)
        {
            var existing = await dataService.GetUserAsync(adminId);
            await dataService.UpsertUserAsync(new BotUser
            {
                UserId = adminId,
                Username = existing?.Username,
                Role = UserRole.Admin,
                Status = UserAccessStatus.Approved,
                CreatedAt = existing?.CreatedAt ?? now,
                UpdatedAt = now
            });
        }
    }
}
