using Microsoft.Extensions.Options;
using Microsoft.Win32;
using System.Runtime.Versioning;
using TelegramBot.Worker.BimLib.Config;
namespace TelegramBot.Worker.BimLib.Services;

/// <summary>
/// Определяет установленные версии Navisworks через Windows Registry
/// (HKLM\SOFTWARE\Autodesk\Navisworks\R{version}) и резолвит пути к Navisworks.exe и FileConvert.exe.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NavisworksPathResolver(
    IOptions<BimIntegrationOptions> options,
    ILogger<NavisworksPathResolver> logger)
{
    private readonly BimIntegrationOptions _options = options.Value;

    /// <inheritdoc/>
    public IReadOnlyList<int> GetInstalledVersions()
    {
        var versions = new List<int>();

        for (var year = _options.MinSupportedVersion; year <= _options.MaxSupportedVersion; year++)
        {
            var path = ResolveNavisworksPath(year);
            if (path != null)
            {
                versions.Add(year);
            }
        }

        var result = versions.OrderByDescending(v => v).ToList();
        logger.LogDebug("Found installed Navisworks versions: {Versions}", string.Join(", ", result));
        return result;
    }

    /// <inheritdoc/>
    public string? ResolveNavisworksPath(int versionYear)
    {
        var installDir = GetNavisworksDirectory(versionYear);
        if (installDir == null)
        {
            logger.LogDebug("Navisworks {Year} not installed", versionYear);
            return null;
        }

        // Navisworks может называться Roamer.exe или Navisworks.exe
        var candidates = new[] { "Roamer.exe", "Navisworks.exe" };
        foreach (var exe in candidates)
        {
            var path = Path.Combine(installDir, exe);
            if (File.Exists(path))
            {
                logger.LogDebug("Navisworks {Year} found at '{Path}'", versionYear, path);
                return path;
            }
        }

        logger.LogWarning("Navisworks {Year} installed but no executable found in '{Dir}'", versionYear, installDir);
        return null;
    }

    /// <inheritdoc/>
    public string? ResolveFileConvertPath(int versionYear)
    {
        // FileConvert.exe может быть в корне установки или в подпапке
        var installDir = GetNavisworksDirectory(versionYear);
        if (installDir == null)
        {
            return null;
        }

        // Пробуем корень установки
        var path = Path.Combine(installDir, "FileConvert.exe");
        if (File.Exists(path))
        {
            logger.LogDebug("FileConvert.exe for Navisworks {Year} found at '{Path}'", versionYear, path);
            return path;
        }

        // Пробуем подпапку FileConvert
        path = Path.Combine(installDir, "FileConvert", "FileConvert.exe");
        if (File.Exists(path))
        {
            logger.LogDebug("FileConvert.exe for Navisworks {Year} found at '{Path}'", versionYear, path);
            return path;
        }

        logger.LogWarning("FileConvert.exe not found for Navisworks {Year}", versionYear);
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
