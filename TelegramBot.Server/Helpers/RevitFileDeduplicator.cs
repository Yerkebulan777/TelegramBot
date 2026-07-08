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
        var groups = new Dictionary<string, List<(string Path, string Name)>>(files.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var path in files)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var prefix = name.Length > _prefixLength ? name[.._prefixLength] : name;
            if (!groups.TryGetValue(prefix, out var bucket))
            {
                bucket = [];
                groups[prefix] = bucket;
            }

            bucket.Add((path, name));
        }

        var result = new List<string>(files.Count);
        foreach (var bucket in groups.Values)
        {
            if (bucket.Count == 1)
            {
                result.Add(bucket[0].Path);
                continue;
            }

            bucket.Sort(static (a, b) =>
            {
                var c = a.Name.Length.CompareTo(b.Name.Length);
                return c != 0 ? c : string.CompareOrdinal(a.Path, b.Path);
            });

            AddUnique(bucket, result);
        }

        return result;
    }

    private static void AddUnique(List<(string Path, string Name)> candidates, List<string> result)
    {
        var accepted = new List<(int NameLength, HashSet<long> Numbers)>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var nameLength = candidate.Name.Length;
            var numbers = ExtractNumbers(candidate.Name);
            var isDuplicate = false;
            if (numbers is not null)
            {
                foreach (var (NameLength, Numbers) in accepted)
                {
                    if (Math.Abs(NameLength - nameLength) <= _nameLengthTolerance
                        && Numbers.Overlaps(numbers))
                    {
                        isDuplicate = true;
                        break;
                    }
                }
            }

            if (isDuplicate)
            {
                continue;
            }

            result.Add(candidate.Path);
            if (numbers is not null)
            {
                accepted.Add((nameLength, numbers));
            }
        }
    }

    private static HashSet<long>? ExtractNumbers(string name)
    {
        HashSet<long>? result = null;
        foreach (var match in RvtNumberPattern().EnumerateMatches(name))
        {
            if (long.TryParse(
                name.AsSpan(match.Index, match.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value))
            {
                _=(result ??= []).Add(value);
            }
        }

        return result;
    }
}
