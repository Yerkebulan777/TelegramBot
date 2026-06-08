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
        services.AddSingleton<IRevitVersionDetector, RevitVersionDetector>();
        services.AddSingleton<RevitPathResolver>();
        services.AddSingleton<RevitProcessTracker>();
        services.AddSingleton<DialogDismisser>();

        services.AddSingleton<INavisworksPathResolver, NavisworksPathResolver>();
        services.AddSingleton<NavisworksProcessTracker>();

        return services;
    }
}
