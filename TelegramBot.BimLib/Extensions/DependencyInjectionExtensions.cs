using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using TelegramBot.BimLib.Interfaces;
using TelegramBot.BimLib.Monitor;
using TelegramBot.BimLib.Services;

namespace TelegramBot.BimLib.Extensions;

/// <summary>Методы расширения для регистрации сервисов BIM-интеграции.</summary>
public static class DependencyInjectionExtensions
{
    /// <summary>Регистрирует сервисы BIM-интеграции (Revit + Navisworks).</summary>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddBimIntegration(this IServiceCollection services)
    {
        // Revit
        _ = services.AddSingleton<IRevitVersionDetector, RevitVersionDetector>();
        _ = services.AddSingleton<IRevitPathResolver, RevitPathResolver>();
        _ = services.AddSingleton<IRevitProcessTracker, RevitProcessTracker>();
        _ = services.AddSingleton<DialogDismisser>();

        // Navisworks
        _ = services.AddSingleton<INavisworksPathResolver, NavisworksPathResolver>();
        _ = services.AddSingleton<INavisworksProcessTracker, NavisworksProcessTracker>();

        return services;
    }
}
