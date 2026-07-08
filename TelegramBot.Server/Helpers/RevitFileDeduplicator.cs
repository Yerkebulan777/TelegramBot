using System.Globalization;
using System.Text.RegularExpressions;

namespace TelegramBot.Server.Helpers;

/// <summary>Удаление похожих RVT-файлов внутри одного уровня папки.</summary>
public static partial class RevitFileDeduplicator
{
    [GeneratedRegex(@"\d{2,}")]
    private static partial Regex RvtNumberPattern();

    private const int _prefixLength = 15;
    private const int _nameLengthTolerance = 5;

    public static List<string> Deduplicate(IReadOnlyCollection<string> files)
    {
        return [.. files
            .Select(static path => (Path: path, Name: Path.GetFileNameWithoutExtension(path)))
            .GroupBy(static file => GetPrefix(file.Name), StringComparer.OrdinalIgnoreCase)
            .SelectMany(KeepFirstBySimilarNumber)];
    }

    private static string GetPrefix(string name)
    {
        return name.Length > _prefixLength ? name[.._prefixLength] : name;
    }

    private static IEnumerable<string> KeepFirstBySimilarNumber(IEnumerable<(string Path, string Name)> files)
    {
        var accepted = new List<(int NameLength, HashSet<long> Numbers)>();

        foreach (var file in files
            .OrderBy(static file => file.Name.Length)
            .ThenBy(static file => file.Path, StringComparer.Ordinal))
        {
            var numbers = ExtractNumbers(file.Name);
            if (numbers.Count > 0
                && accepted.Any(known => Math.Abs(known.NameLength - file.Name.Length) <= _nameLengthTolerance
                    && known.Numbers.Overlaps(numbers)))
            {
                continue;
            }

            yield return file.Path;

            if (numbers.Count > 0)
            {
                accepted.Add((file.Name.Length, numbers));
            }
        }
    }

    private static HashSet<long> ExtractNumbers(string name)
    {
        var result = new HashSet<long>();
        foreach (var match in RvtNumberPattern().EnumerateMatches(name))
        {
            if (long.TryParse(
                name.AsSpan(match.Index, match.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value))
            {
                result.Add(value);
            }
        }

        return result;
    }
}
