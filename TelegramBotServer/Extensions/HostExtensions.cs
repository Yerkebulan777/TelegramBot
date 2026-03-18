using TelegramBotServer.Interfaces;

namespace TelegramBotServer.Extensions;

public static class HostExtensions
{
    /// <summary>Инициализирует базу данных при запуске приложения.</summary>
    public static async Task InitializeDatabaseAsync(this IHost host)
    {
        using IServiceScope scope = host.Services.CreateScope();
        IDataService dataService = scope.ServiceProvider.GetRequiredService<IDataService>();
        await dataService.InitializeDatabaseAsync();
    }
}
