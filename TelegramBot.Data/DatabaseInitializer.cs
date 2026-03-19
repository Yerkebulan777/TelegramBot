using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TelegramBot.Core.Interfaces;

namespace TelegramBot.Data;

/// <summary>
/// Extension method to initialize the database at application startup.
/// </summary>
public static class DatabaseInitializer
{
    /// <summary>Инициализирует базу данных при запуске приложения.</summary>
    public static async Task InitializeDatabaseAsync(this IHost host)
    {
        using IServiceScope scope = host.Services.CreateScope();
        IDataService dataService = scope.ServiceProvider.GetRequiredService<IDataService>();
        await dataService.InitializeDatabaseAsync();
    }
}
