using Microsoft.Extensions.Logging;
using OpenMcdf;
using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text;
using TelegramBot.BimLib.Models;

namespace TelegramBot.BimLib.Services;

/// <summary>
/// Определяет версию Revit по OLE-потоку BasicFileInfo внутри .rvt/.rfa-файла.
/// Использует OpenMcdf для чтения OLE Structured Storage.
/// Алгоритм:
///   1. Открыть файл как OLE Compound File через OpenMcdf
///   2. Прочитать поток "BasicFileInfo"
///   3. Извлечь текст между маркерами (первый маркер \r\n, fallback \x04\r\x00\n\x00)
///   4. Найти строку "Format:" и извлечь числовое значение года
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RevitVersionDetector(
    RevitPathResolver pathResolver,
    ILogger<RevitVersionDetector> logger)
{
    private const int MaxCacheEntries = 4096;
    private readonly ConcurrentDictionary<CacheKey, RevitDetectedVersion> _cache = new();

    /// <inheritdoc/>
    public RevitDetectedVersion? DetectVersion(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            logger.LogWarning("DetectVersion: empty path");
            return null;
        }

        FileInfo fileInfo;
        try
        {
            fileInfo = new FileInfo(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            logger.LogWarning(ex, "DetectVersion: invalid path '{Path}'", filePath);
            return null;
        }

        if (!fileInfo.Exists)
        {
            logger.LogWarning("DetectVersion: file not found '{Path}'", filePath);
            return null;
        }

        var ext = fileInfo.Extension.ToLowerInvariant();
        if (ext is not (".rvt" or ".rfa" or ".rte"))
        {
            logger.LogDebug("DetectVersion skip: unsupported ext '{Ext}' ('{Path}')", ext, filePath);
            return null;
        }

        CacheKey cacheKey;
        try
        {
            cacheKey = CacheKey.From(fileInfo);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "DetectVersion: cant read metadata '{Path}'", filePath);
            return null;
        }

        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            logger.LogDebug("Revit {Year} from cache ('{Path}')", cached.Year, filePath);
            return cached;
        }

        try
        {
            ct.ThrowIfCancellationRequested();

            var versionText = GetRevitVersionText(filePath);

            if (versionText == null)
            {
                logger.LogDebug("DetectVersion: no Format line in '{Path}'", filePath);
                return null;
            }

            if (!int.TryParse(versionText, out var year))
            {
                logger.LogWarning("DetectVersion: cant parse year '{Version}' from '{Path}'", versionText, filePath);
                return null;
            }

            logger.LogDebug("Revit {Year} from '{Path}'", year, filePath);

            var detected = new RevitDetectedVersion
            {
                Year = year,
                ExecutablePath = pathResolver.ResolveExecutablePath(year)
            };

            AddToCache(cacheKey, detected);
            return detected;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DetectVersion fail: '{Path}'", filePath);
            return null;
        }
    }

    private void AddToCache(CacheKey key, RevitDetectedVersion value)
    {
        if (_cache.Count >= MaxCacheEntries)
        {
            _cache.Clear();
        }

        _cache[key] = value;
    }

    /// <summary>
    /// Читает поток BasicFileInfo из OLE-файла через OpenMcdf и извлекает год
    /// из поля "Format:". Текст может начинаться с чётного или нечётного байта,
    /// поэтому проверяются оба возможных выравнивания UTF-16 LE.
    /// </summary>
    private string? GetRevitVersionText(string filePath)
    {
        using var root = RootStorage.OpenRead(filePath);
        using var stream = root.OpenStream("BasicFileInfo");

        var streamData = new byte[stream.Length];
        stream.ReadExactly(streamData);

        for (var offset = 0; offset < 2 && offset < streamData.Length; offset++)
        {
            var infoText = Encoding.Unicode.GetString(streamData, offset, streamData.Length - offset);
            using var reader = new StringReader(infoText);

            while (reader.ReadLine() is { } line)
            {
                line = line.Trim();
                if (!line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var digits = line.Where(char.IsDigit).ToArray();
                if (digits.Length > 0)
                {
                    return new string(digits);
                }
            }
        }

        return null;
    }

    private readonly record struct CacheKey(string FullPath, DateTime LastWriteTimeUtc, long Length)
    {
        public static CacheKey From(FileInfo fileInfo)
        {
            return new CacheKey(
                Path.GetFullPath(fileInfo.FullName).ToUpperInvariant(),
                fileInfo.LastWriteTimeUtc,
                fileInfo.Length);
        }
    }
}
