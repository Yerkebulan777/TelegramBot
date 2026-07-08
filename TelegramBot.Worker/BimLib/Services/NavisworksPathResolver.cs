using Microsoft.Extensions.Options;
using Microsoft.Win32;
using System.Runtime.Versioning;
using TelegramBot.Worker.BimLib.Config;
namespace TelegramBot.Worker.BimLib.Services;

/// <summary>
/// Резолвит последнюю поддерживаемую установку Navisworks через Windows Registry.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NavisworksPathResolver(
    IOptions<BimIntegrationOptions> options,
    ILogger<NavisworksPathResolver> logger)
{
    private readonly BimIntegrationOptions _options = options.Value;

    public string? ResolveLatestExecutable()
    {
        for (var year = _options.MaxSupportedVersion; year >= _options.MinSupportedVersion; year--)
        {
            var installDir = GetNavisworksDirectory(year);
            if (installDir == null)
            {
                continue;
            }

            foreach (var relativePath in new[] { "FileConvert.exe", @"FileConvert\FileConvert.exe", "Roamer.exe", "Navisworks.exe" })
            {
                var path = Path.Combine(installDir, relativePath);
                if (File.Exists(path))
                {
                    logger.LogDebug("Navisworks {Year} found: '{Path}'", year, path);
                    return path;
                }
            }

            logger.LogWarning("Navisworks {Year} installed, no exe in '{Dir}'", year, installDir);
        }

        return null;
    }

    /// <summary>
    /// Ищет путь установки Navisworks через реестр Windows.
    /// Проверяет: SOFTWARE\Autodesk\Navisworks\R{version} и SOFTWARE\Autodesk\NavisworksManage\R{version}.
    /// </summary>
    private static string? GetNavisworksDirectory(int versionYear)
    {
        // Формат: HKLM\SOFTWARE\Autodesk\Navisworks\R2023
        var path = TryGetRegistryInstallPath($@"SOFTWARE\Autodesk\Navisworks\R{versionYear}");
        if (path != null)
        {
            return path;
        }

        // Альтернативный формат: HKLM\SOFTWARE\Autodesk\NavisworksManage\R2023
        path = TryGetRegistryInstallPath($@"SOFTWARE\Autodesk\NavisworksManage\R{versionYear}");
        if (path != null)
        {
            return path;
        }

        // WOW6432Node для 32-битных версий
        path = TryGetRegistryInstallPath($@"SOFTWARE\WOW6432Node\Autodesk\Navisworks\R{versionYear}");
        return path ??null;
    }

    /// <summary>
    /// Открывает указанный ключ реестра и читает InstallationLocation.
    /// </summary>
    private static string? TryGetRegistryInstallPath(string registryKey)
    {
        using var skey = Registry.LocalMachine.OpenSubKey(registryKey);
        if (skey == null)
        {
            return null;
        }

        var location = skey.GetValue("InstallationLocation")?.ToString();
        if (!string.IsNullOrWhiteSpace(location) && Directory.Exists(location))
        {
            return location;
        }

        // Некоторые версии хранят путь в подразделах
        var subKeyNames = skey.GetSubKeyNames();
        foreach (var subKey in subKeyNames)
        {
            using var sub = skey.OpenSubKey(subKey);
            var loc = sub?.GetValue("InstallationLocation")?.ToString();
            if (!string.IsNullOrWhiteSpace(loc) && Directory.Exists(loc))
            {
                return loc;
            }
        }

        return null;
    }
}
