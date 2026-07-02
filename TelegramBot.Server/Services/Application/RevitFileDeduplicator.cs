using System.Text.RegularExpressions;

namespace TelegramBot.Server.Services.Application;

public static partial class RevitFileDeduplicator
{
    [GeneratedRegex(@"\d{2,}")]
    private static partial Regex RvtNumberPattern();

    /// <summary>
    /// Removes duplicate Revit files by exact file name first, then by matching numeric tokens
    /// within a prefix group (only when the name-length difference is ≤5 characters).
    /// Files closer to the RVT root (lower depth) win over subfolder duplicates;
    /// among equal depth the shortest name wins, equal-length names keep their original order.
    /// </summary>
    public static List<string> Deduplicate(IReadOnlyCollection<(string Path, int Depth)> files)
    {
        var byName = new Dictionary<string, (string Path, int Depth)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, depth) in files)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!byName.TryGetValue(name, out var existing) || depth < existing.Depth)
            {
                byName[name] = (path, depth);
            }
        }

        var candidates = byName
            .Select(entry => new RevitFileCandidate(entry.Value.Path, entry.Key, entry.Value.Depth, ExtractNumbers(entry.Key)))
            .ToList();

        var result = new List<string>(candidates.Count);

        foreach (var group in candidates.GroupBy(
            candidate => candidate.Name.Length > 15 ? candidate.Name[..15] : candidate.Name,
            StringComparer.OrdinalIgnoreCase))
        {
            var accepted = new List<RevitFileCandidate>();

            foreach (var candidate in group.OrderBy(candidate => candidate.Depth).ThenBy(candidate => candidate.Name.Length))
            {
                var isDuplicate = false;
                foreach (var acceptedCandidate in accepted)
                {
                    if (Math.Abs(acceptedCandidate.Name.Length - candidate.Name.Length) <= 5
                        && acceptedCandidate.Numbers.Overlaps(candidate.Numbers))
                    {
                        isDuplicate = true;
                        break;
                    }
                }

                if (!isDuplicate)
                {
                    accepted.Add(candidate);
                }
            }

            result.AddRange(accepted.Select(candidate => candidate.Path));
        }

        return result;
    }

    private static HashSet<long> ExtractNumbers(string name)
    {
        var result = new HashSet<long>();
        foreach (Match match in RvtNumberPattern().Matches(name))
        {
            if (long.TryParse(match.Value, out var value))
            {
                _ = result.Add(value);
            }
        }

        return result;
    }

    private sealed record RevitFileCandidate(string Path, string Name, int Depth, HashSet<long> Numbers);
}
