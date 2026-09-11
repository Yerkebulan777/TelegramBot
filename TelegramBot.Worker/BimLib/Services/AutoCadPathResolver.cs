using System.Diagnostics;
using System.Runtime.Versioning;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using TelegramBot.BimLib.Models;

namespace TelegramBot.BimLib.Services;

/// <summary>
/// Находит установленный AutoCAD 2019–2027 и совместимый AutoBIMFusion.bundle
/// (логика как в AutoBIMFusion tools/MergeDwgBatchHost.ps1).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AutoCadPathResolver(ILogger<AutoCadPathResolver> logger)
{
    private const string BundleName = "AutoBIMFusion.bundle";
    private const string PluginAppName = "AutoBIMFusion";

    // FileVersion и ProductVersion расходятся (в AutoCAD 2019 — 29.0 против 23.0), поэтому год
    // определяется по ProductVersion.
    private static readonly IReadOnlyDictionary<string, int> ProductSeriesToYear =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["23.0"] = 2019,
            ["23.1"] = 2020,
            ["24.0"] = 2021,
            ["24.1"] = 2022,
            ["24.2"] = 2023,
            ["24.3"] = 2024,
            ["25.0"] = 2025,
            ["25.1"] = 2026,
            ["26.0"] = 2027,
        };

    private static readonly string[] RequiredPluginAssemblies =
    [
        "AutoBIMFusion.Common.dll",
        "AutoBIMFusion.Merge.dll",
        "Serilog.dll",
        "Serilog.Sinks.File.dll",
    ];

    /// <summary>Новейший установленный AutoCAD с совместимым плагином, либо null.</summary>
    public AutoCadBatchHost? ResolveBatchHost()
    {
        var installations = DiscoverInstallations();
        if (installations.Count == 0)
        {
            logger.LogWarning("AutoCAD 2019-2027 not found in registry or Program Files");
            return null;
        }

        var plugins = DiscoverPlugins();
        if (plugins.Count == 0)
        {
            logger.LogWarning("{Bundle} not found in Autodesk ApplicationPlugins", BundleName);
            return null;
        }

        foreach (var installation in installations.OrderByDescending(item => item.Series))
        {
            foreach (var plugin in plugins)
            {
                if (installation.Series >= plugin.SeriesMin && installation.Series <= plugin.SeriesMax)
                {
                    logger.LogDebug("AutoCAD {Year}: exe='{Exe}', plugin='{Plugin}'",
                        installation.Year, installation.Exe, plugin.Path);
                    return new AutoCadBatchHost(installation.Exe, installation.Year, plugin.Path);
                }
            }
        }

        logger.LogWarning("Installed AutoCAD versions are outside the SeriesMin/SeriesMax range of {Bundle}", BundleName);
        return null;
    }

    private List<Installation> DiscoverInstallations()
    {
        var installations = new List<Installation>();

        foreach (var root in DiscoverInstallationRoots())
        {
            var exe = Path.Combine(root, "acad.exe");
            if (!File.Exists(exe))
            {
                continue;
            }

            try
            {
                var version = FileVersionInfo.GetVersionInfo(exe);
                var series = $"{version.ProductMajorPart}.{version.ProductMinorPart}";
                if (ProductSeriesToYear.TryGetValue(series, out var year))
                {
                    installations.Add(new Installation(
                        exe, year, new Version(version.ProductMajorPart, version.ProductMinorPart)));
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Cannot read AutoCAD version: '{Exe}'", exe);
            }
        }

        return installations;
    }

    /// <summary>Реестр покрывает установки вне Program Files и вертикальные продукты.</summary>
    private static HashSet<string> DiscoverInstallationRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var autoCadKey = hive.OpenSubKey(@"SOFTWARE\Autodesk\AutoCAD");
            if (autoCadKey is not null)
            {
                AddAcadLocations(autoCadKey, roots);
            }
        }

        if (Environment.GetEnvironmentVariable("ACAD_HOME") is string acadHome && !string.IsNullOrWhiteSpace(acadHome))
        {
            _ = roots.Add(acadHome);
        }

        var autodeskRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk");
        if (Directory.Exists(autodeskRoot))
        {
            roots.UnionWith(Directory.EnumerateDirectories(autodeskRoot, "AutoCAD*"));
        }

        return roots;
    }

    private static void AddAcadLocations(RegistryKey key, HashSet<string> roots)
    {
        if (key.GetValue("AcadLocation") is string location && !string.IsNullOrWhiteSpace(location))
        {
            _ = roots.Add(location);
        }

        foreach (var subKeyName in key.GetSubKeyNames())
        {
            using var subKey = key.OpenSubKey(subKeyName);
            if (subKey is not null)
            {
                AddAcadLocations(subKey, roots);
            }
        }
    }

    private List<Plugin> DiscoverPlugins()
    {
        var plugins = new List<Plugin>();

        foreach (var pluginsRoot in DiscoverApplicationPluginsRoots())
        {
            var bundle = Path.Combine(pluginsRoot, BundleName);
            var manifest = Path.Combine(bundle, "PackageContents.xml");
            if (!File.Exists(manifest))
            {
                continue;
            }

            try
            {
                plugins.AddRange(ReadManifest(bundle, manifest));
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Cannot use installed bundle '{Manifest}'", manifest);
            }
        }

        return plugins;
    }

    private static IEnumerable<string> DiscoverApplicationPluginsRoots()
    {
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.ApplicationData,
                     Environment.SpecialFolder.CommonApplicationData,
                     Environment.SpecialFolder.ProgramFiles,
                 })
        {
            yield return Path.Combine(
                Environment.GetFolderPath(folder), @"Autodesk\ApplicationPlugins");
        }
    }

    private static IEnumerable<Plugin> ReadManifest(string bundle, string manifest)
    {
        var document = XDocument.Load(manifest);

        foreach (var components in document.Root?.Elements("Components") ?? [])
        {
            var requirements = components.Element("RuntimeRequirements");
            if (requirements is null
                || !string.Equals((string?)requirements.Attribute("OS"), "Win64", StringComparison.OrdinalIgnoreCase)
                || (string?)requirements.Attribute("Platform") is not string platform
                || !platform.StartsWith("AutoCAD", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryParseSeries((string?)requirements.Attribute("SeriesMin"), out var seriesMin)
                || !TryParseSeries((string?)requirements.Attribute("SeriesMax"), out var seriesMax))
            {
                continue;
            }

            foreach (var entry in components.Elements("ComponentEntry"))
            {
                if (!string.Equals((string?)entry.Attribute("AppName"), PluginAppName, StringComparison.Ordinal)
                    || (string?)entry.Attribute("ModuleName") is not string moduleName
                    || string.IsNullOrWhiteSpace(moduleName))
                {
                    continue;
                }

                var dll = Path.GetFullPath(Path.Combine(bundle, moduleName));
                if (File.Exists(dll) && HasRequiredAssemblies(dll))
                {
                    yield return new Plugin(dll, seriesMin, seriesMax);
                }
            }
        }
    }

    private static bool HasRequiredAssemblies(string pluginPath) =>
        Path.GetDirectoryName(pluginPath) is string contents
        && RequiredPluginAssemblies.All(name => File.Exists(Path.Combine(contents, name)));

    private static bool TryParseSeries(string? rawSeries, out Version series)
    {
        series = new Version(0, 0);

        return !string.IsNullOrWhiteSpace(rawSeries)
            && Version.TryParse(rawSeries.TrimStart('R', 'r'), out series!);
    }

    private sealed record Installation(string Exe, int Year, Version Series);

    private sealed record Plugin(string Path, Version SeriesMin, Version SeriesMax);
}
