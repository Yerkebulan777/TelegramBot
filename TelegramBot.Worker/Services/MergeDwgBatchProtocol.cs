using System.Text;
using System.Text.Json;
using TelegramBot.Worker.Helpers;
using TelegramBot.Worker.Models;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Обмен с командой AutoBIMFusion <c>MERGEDWG_BATCH</c>: Worker пишет AutoCAD script,
/// плагин отвечает status JSON. Эталон — AutoBIMFusion tools/Start-MergeDwgBatch.ps1.
/// </summary>
public static class MergeDwgBatchProtocol
{
    // AutoCAD читает .scr в системной ANSI (cp1251). UTF-8 без BOM ломает кириллицу в путях
    // (mojibake) -> MERGEDWG_BATCH падает с DirectoryNotFoundException на существующей папке.
    // UTF-8 с BOM корректно распознаётся AutoCAD 2025-2027.
    private static readonly Encoding ScriptEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    private static readonly JsonSerializerOptions StatusJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Аргументы AutoCAD для пакетного прогона скрипта.</summary>
    public static string BuildArguments(string scriptPath) => $"/nologo /b \"{scriptPath}\"";

    /// <summary>
    /// Пишет script текущей попытки и отводит status предыдущей попытки,
    /// чтобы устаревший ответ не был прочитан как результат этого запуска.
    /// </summary>
    public static void WriteRequest(string scriptPath, string statusPath, string pluginPath, string sourceFolder)
    {
        var directory = Path.GetDirectoryName(scriptPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            _ = Directory.CreateDirectory(directory);
        }

        if (File.Exists(statusPath))
        {
            File.Move(statusPath, statusPath + ".previous", overwrite: true);
        }

        string[] lines =
        [
            "FILEDIA",
            "0",
            "CMDDIA",
            "0",
            "SECURELOAD",
            "0",
            "NETLOAD",
            SanitizeScriptLine(pluginPath),
            "MERGEDWG_BATCH",
            SanitizeScriptLine(sourceFolder),
            SanitizeScriptLine(statusPath),
            "._QUIT",
            "_Y",
        ];

        File.WriteAllLines(scriptPath, lines, ScriptEncoding);
    }

    /// <summary>Исход чтения status JSON.</summary>
    public enum StatusReadKind
    {
        Missing,
        Parsed,
        InvalidJson,
        TransientIo,
    }

    public readonly record struct StatusRead(
        StatusReadKind Kind,
        MergeDwgBatchStatus? Status,
        string? ErrorMessage);

    /// <summary>Читает ответ плагина с тем же retry/FileShare, что у ResultFile.</summary>
    public static async Task<StatusRead> ReadStatusAsync(string statusPath, CancellationToken cancellationToken)
    {
        var read = ReadStatusOnce(statusPath);
        for (var attempt = 0;
             read.Kind == StatusReadKind.TransientIo && await PluginOutputFileRead.ShouldRetryAsync(attempt, cancellationToken);
             attempt++)
        {
            read = ReadStatusOnce(statusPath);
        }

        return read;
    }

    private static StatusRead ReadStatusOnce(string statusPath)
    {
        if (!File.Exists(statusPath))
        {
            return new(StatusReadKind.Missing, null, "AutoCAD did not write the MERGEDWG status file");
        }

        try
        {
            using var stream = PluginOutputFileRead.Open(statusPath);
            var status = JsonSerializer.Deserialize<MergeDwgBatchStatus>(stream, StatusJsonOptions);

            return status is null
                ? new(StatusReadKind.InvalidJson, null, "MERGEDWG status file is empty")
                : new(StatusReadKind.Parsed, status, null);
        }
        catch (JsonException ex)
        {
            return new(StatusReadKind.InvalidJson, null, $"MERGEDWG status file cannot be read: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(StatusReadKind.TransientIo, null, $"MERGEDWG status file cannot be read: {ex.Message}");
        }
    }

    // Перевод строки в аргументе превратил бы остаток пути в отдельную команду AutoCAD.
    private static string SanitizeScriptLine(string value) =>
        value.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
}
