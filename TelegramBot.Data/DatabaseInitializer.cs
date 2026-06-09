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
        using var scope = host.Services.CreateScope();
        var initializer = scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>();
        await initializer.InitializeDatabaseAsync();
    }

    /// <summary>
    /// Гарантирует, что сконфигурированные администраторы присутствуют в БД со статусом Approved+Admin.
    /// </summary>
    public static async Task SeedAdminUsersAsync(this IHost host)
    {
        using var scope = host.Services.CreateScope();
        var userDataService = scope.ServiceProvider.GetRequiredService<IUserDataService>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var adminIds = configuration.GetSection("TelegramBot:AdminUserIds").Get<long[]>() ?? [];
        if (adminIds.Length == 0)
        {
            return;
        }

        await userDataService.UpsertUsersBatchAsync(adminIds, (int)UserRole.Admin, (int)UserAccessStatus.Approved);
    }
}
