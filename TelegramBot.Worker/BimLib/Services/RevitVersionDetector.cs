using OpenMcdf;
using System.Runtime.Versioning;
using System.Text;
using TelegramBot.Worker.BimLib.Interfaces;
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
    ILogger<RevitVersionDetector> logger) : IRevitVersionDetector
{
    /// <inheritdoc/>
    public Task<RevitDetectedVersion?> DetectVersionAsync(string filePath, CancellationToken ct = default)
    {
        // Note: OpenMcdf не поддерживает async, поэтому метод синхронный с Task.FromResult
        if (string.IsNullOrWhiteSpace(filePath))
        {
            logger.LogWarning("DetectVersion failed: empty path");
            return Task.FromResult<RevitDetectedVersion?>(null);
        }

        if (!File.Exists(filePath))
        {
            logger.LogWarning("DetectVersion failed: file not found '{Path}'", filePath);
            return Task.FromResult<RevitDetectedVersion?>(null);
        }

        var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
        if (ext is not (".rvt" or ".rfa" or ".rte"))
        {
            logger.LogDebug("DetectVersion skipped: unsupported extension '{Ext}' for '{Path}'", ext, filePath);
            return Task.FromResult<RevitDetectedVersion?>(null);
        }

        try
        {
            ct.ThrowIfCancellationRequested();

            var versionText = GetRevitVersionText(filePath);

            if (versionText == null)
            {
                logger.LogDebug("DetectVersion failed: no Format: line found in '{Path}'", filePath);
                return Task.FromResult<RevitDetectedVersion?>(null);
            }

            if (!int.TryParse(versionText, out var year))
            {
                logger.LogWarning("DetectVersion failed: could not parse year '{Version}' from '{Path}'", versionText, filePath);
                return Task.FromResult<RevitDetectedVersion?>(null);
            }

            logger.LogDebug("Detected Revit {Year} from '{Path}'", year, filePath);

            return Task.FromResult<RevitDetectedVersion?>(new RevitDetectedVersion
            {
                Year = year,
                DisplayName = $"Autodesk Revit {year}",
                IsSupported = year is >= 2017 and <= 2026,
                ExecutablePath = pathResolver.ResolveExecutablePath(year)
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DetectVersion failed for '{Path}'", filePath);
            return Task.FromResult<RevitDetectedVersion?>(null);
        }
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
    /// Исключения от OpenMcdf пробрасываются наружу — outer catch в DetectVersionAsync
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
}
