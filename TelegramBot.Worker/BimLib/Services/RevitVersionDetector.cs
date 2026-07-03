using OpenMcdf;
using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text;
using TelegramBot.Worker.BimLib.Models;

namespace TelegramBot.Worker.BimLib.Services;

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
            logger.LogWarning("DetectVersion failed: empty path");
            return null;
        }

        FileInfo fileInfo;
        try
        {
            fileInfo = new FileInfo(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            logger.LogWarning(ex, "DetectVersion failed: invalid path '{Path}'", filePath);
            return null;
        }

        if (!fileInfo.Exists)
        {
            logger.LogWarning("DetectVersion failed: file not found '{Path}'", filePath);
            return null;
        }

        var ext = fileInfo.Extension.ToLowerInvariant();
        if (ext is not (".rvt" or ".rfa" or ".rte"))
        {
            logger.LogDebug("DetectVersion skipped: unsupported extension '{Ext}' for '{Path}'", ext, filePath);
            return null;
        }

        CacheKey cacheKey;
        try
        {
            cacheKey = CacheKey.From(fileInfo);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "DetectVersion failed: cannot read file metadata '{Path}'", filePath);
            return null;
        }

        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            logger.LogDebug("Detected Revit {Year} from cache for '{Path}'", cached.Year, filePath);
            return cached;
        }

        try
        {
            ct.ThrowIfCancellationRequested();

            var versionText = GetRevitVersionText(filePath);

            if (versionText == null)
            {
                logger.LogDebug("DetectVersion failed: no Format: line found in '{Path}'", filePath);
                return null;
            }

            if (!int.TryParse(versionText, out var year))
            {
                logger.LogWarning("DetectVersion failed: could not parse year '{Version}' from '{Path}'", versionText, filePath);
                return null;
            }

            logger.LogDebug("Detected Revit {Year} from '{Path}'", year, filePath);

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
            logger.LogWarning(ex, "DetectVersion failed for '{Path}'", filePath);
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
    /// Читает поток BasicFileInfo из OLE-файла через OpenMcdf и извлекает
    /// строку с номером версии из поля "Format:".
    /// Возвращает только цифры (год), например "2024".
    /// </summary>
    private string? GetRevitVersionText(string filePath)
    {
        var infoText = GetBasicFileInfoText(filePath);
        if (infoText == null)
        {
            return null;
        }

        using var reader = new StringReader(infoText);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (!line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Извлекаем только цифры из строки "Format: 2024"
            var digits = new char[line.Length];
            var digitCount = 0;
            foreach (var ch in line)
            {
                if (char.IsDigit(ch))
                {
                    digits[digitCount++] = ch;
                }
            }

            return digitCount > 0 ? new string(digits, 0, digitCount) : null;
        }

        return null;
    }

    /// <summary>
    /// Открывает .rvt-файл как OLE Compound File через OpenMcdf,
    /// читает поток "BasicFileInfo" и преобразует его в читаемый текст.
    /// Между маркерами данные в Unicode (UTF-16 LE).
    /// Исключения от OpenMcdf пробрасываются наружу — outer catch в DetectVersion
    /// логирует их как Warning с контекстом вызова.
    /// </summary>
    private static string? GetBasicFileInfoText(string filePath)
    {
        using var root = RootStorage.OpenRead(filePath);
        using var stream = root.OpenStream("BasicFileInfo");

        var streamData = new byte[stream.Length];
        _ = stream.Read(streamData, 0, (int)stream.Length);

        // Конвертируем в ASCII для поиска маркеров
        var asciiString = Encoding.ASCII.GetString(streamData);

        // Пробуем маркеры: \r\n, затем \x04\r\x00\n\x00 как fallback
        var markers = new[] { "\r\n", "\x04\r\x00\n\x00" };

        foreach (var marker in markers)
        {
            var first = asciiString.IndexOf(marker, StringComparison.Ordinal);
            if (first < 0)
            {
                continue;
            }

            var second = asciiString.IndexOf(marker, first + marker.Length, StringComparison.Ordinal);
            if (second < 0)
            {
                continue;
            }

            // Текст находится между двумя маркерами, сразу после первого маркера
            var startIndex = first + marker.Length;
            var length = second - startIndex;

            if (length <= 0)
            {
                continue;
            }

            // Текст между маркерами — Unicode (UTF-16 LE)
            var textBytes = streamData.Skip(startIndex).Take(length).ToArray();
            return Encoding.Unicode.GetString(textBytes);
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
