using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using System.Runtime.Versioning;
using TelegramBot.BimLib.Config;

namespace TelegramBot.BimLib.Services;

/// <summary>
/// Определяет установленные версии Revit через Windows Registry
/// (HKLM\SOFTWARE\Autodesk\Revit\{version}) и резолвит путь к Revit.exe.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RevitPathResolver(
    IOptions<BimIntegrationOptions> options,
    ILogger<RevitPathResolver> logger)
{
    private readonly BimIntegrationOptions _options = options.Value;

    /// <summary>Находит полный путь к Revit.exe для указанной версии через реестр Windows.</summary>
    public string? ResolveExecutablePath(int versionYear)
    {
        if (versionYear < _options.MinSupportedVersion || versionYear > _options.MaxSupportedVersion)
        {
            logger.LogWarning("Unsupported Revit: {Year} (supported: {Min}-{Max})",
                versionYear, _options.MinSupportedVersion, _options.MaxSupportedVersion);
            return null;
        }

        var installDir = GetRevitDirectoryFromRegistry(versionYear.ToString());

        if (installDir == null)
        {
            logger.LogDebug("Revit {Year} not installed (reg)", versionYear);
            return null;
        }

        var revitPath = Path.Combine(installDir, "Revit.exe");

        if (!File.Exists(revitPath))
        {
            logger.LogWarning("Revit {Year} reg path '{Path}' - Revit.exe missing",
                versionYear, revitPath);
            return null;
        }

        logger.LogDebug("Revit {Year} found: '{Path}'", versionYear, revitPath);
        return revitPath;
    }

    /// <summary>
    /// Ищет путь установки Revit через реестр Windows.
    /// Проверяет HKLM\SOFTWARE\Autodesk\Revit\{version} и HKLM\SOFTWARE\Autodesk\Revit{version}.
    /// </summary>
    private static string? GetRevitDirectoryFromRegistry(string version)
    {
        // Сначала пробуем SOFTWARE\Autodesk\Revit\{version}
        var path = TryGetRegistryPath($@"SOFTWARE\Autodesk\Revit\{version}");
        if (path != null)
        {
            return path;
        }

        // Fallback: SOFTWARE\Autodesk\Revit{version}
        path = TryGetRegistryPath($@"SOFTWARE\Autodesk\Revit{version}");
        if (path != null)
        {
            return path;
        }

        // Попробовать WOW6432Node для 32-битных версий на 64-битной OS
        path = TryGetRegistryPath($@"SOFTWARE\WOW6432Node\Autodesk\Revit\{version}");
        return path ?? null;
    }

    /// <summary>
    /// Открывает указанный ключ реестра, ищет подраздел с "REVIT-" и читает InstallationLocation.
    /// </summary>
    private static string? TryGetRegistryPath(string registryKey)
    {
        using var skey = Registry.LocalMachine.OpenSubKey(registryKey);
        if (skey == null)
        {
            return null;
        }

        var subKeyNames = skey.GetSubKeyNames();

        // Ищем подраздел, содержащий "REVIT-"
        foreach (var subKey in subKeyNames)
        {
            if (!subKey.Contains("REVIT-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var rvtKey = skey.OpenSubKey(subKey);
            var location = rvtKey?.GetValue("InstallationLocation")?.ToString();
            if (!string.IsNullOrWhiteSpace(location))
            {
                return location;
            }
        }

        return null;
    }
}
