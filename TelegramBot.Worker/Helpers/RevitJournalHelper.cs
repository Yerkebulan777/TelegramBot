using System.Text;
using System.Text.RegularExpressions;

namespace TelegramBot.Worker.Helpers;

/// <summary>
/// Извлекает диагностику из журнала Revit после краша процесса.
/// Worker-лог видит только exit code (например ACCESS_VIOLATION), а реальная причина —
/// последняя операция перед смертью — записана в журнале Revit
/// (<c>%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit {год}\Journals\journal.*.txt</c>).
/// </summary>
internal static partial class RevitJournalHelper
{
    [GeneratedRegex(@"Revit (\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex RevitVersionRegex();

    // Маркеры строк журнала, указывающих на причину сбоя
    private static readonly string[] ErrorMarkers =
    [
        "ExceptionCode",
        "Exception occurred",
        "Fatal",
        "SLOG",
        "CrashReport",
        "err ",
        "Error:",
    ];

    private const int TailReadBytes = 128 * 1024;
    private const int MaxEvidenceChars = 4096;
    private const int AlwaysIncludeLastLines = 15;

    /// <summary>
    /// Возвращает хвост журнала Revit, относящегося к упавшему процессу,
    /// либо null, если журнал не найден (не Revit, нет доступа и т.п.).
    /// Журнал подбирается по версии Revit из пути exe и по времени изменения файла
    /// (журнал пишется в течение жизни процесса).
    /// </summary>
    public static string? TryGetCrashEvidence(string exePath, DateTime processStartUtc)
    {
        try
        {
            var versionMatch = RevitVersionRegex().Match(exePath);
            if (!versionMatch.Success)
            {
                return null;
            }

            var journalsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Autodesk", "Revit", $"Autodesk Revit {versionMatch.Groups[1].Value}", "Journals");

            if (!Directory.Exists(journalsDir))
            {
                return null;
            }

            // Журнал создаётся при старте Revit и дописывается до смерти процесса —
            // берём самый свежий из изменённых после старта (с минутой слабины на рассинхрон часов).
            var cutoffUtc = processStartUtc.AddMinutes(-1);
            var journal = new DirectoryInfo(journalsDir)
                .EnumerateFiles("journal*.txt")
                .Where(f => f.LastWriteTimeUtc >= cutoffUtc)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();

            if (journal == null)
            {
                return null;
            }

            var lines = ReadTailLines(journal.FullName);
            if (lines.Count == 0)
            {
                return null;
            }

            var evidence = ExtractEvidence(lines);
            return $"journal={journal.FullName}\n{evidence}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Читает последние <see cref="TailReadBytes"/> журнала и разбивает на строки.</summary>
    private static List<string> ReadTailLines(string path)
    {
        // FileShare.ReadWrite: журнал может быть ещё открыт умирающим процессом
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > TailReadBytes)
        {
            _ = stream.Seek(-TailReadBytes, SeekOrigin.End);
        }

        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    /// <summary>
    /// Собирает строки с маркерами ошибок + последние строки журнала (последняя операция перед смертью).
    /// Итог обрезается до <see cref="MaxEvidenceChars"/>.
    /// </summary>
    private static string ExtractEvidence(List<string> lines)
    {
        var picked = new List<string>();

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var isLastBlock = i >= lines.Count - AlwaysIncludeLastLines;
            if (isLastBlock || ErrorMarkers.Any(m => line.Contains(m, StringComparison.OrdinalIgnoreCase)))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    picked.Add(trimmed);
                }
            }
        }

        var sb = new StringBuilder();
        // Идём с конца — свежие строки важнее, если упрёмся в лимит
        for (var i = picked.Count - 1; i >= 0; i--)
        {
            if (sb.Length + picked[i].Length + 1 > MaxEvidenceChars)
            {
                _ = sb.Insert(0, "... (older journal lines omitted)\n");
                break;
            }

            _ = sb.Insert(0, picked[i] + '\n');
        }

        return sb.ToString().TrimEnd();
    }
}
