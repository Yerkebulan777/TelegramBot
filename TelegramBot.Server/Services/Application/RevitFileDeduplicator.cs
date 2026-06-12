using System.Text.RegularExpressions;

namespace TelegramBot.Server.Services.Application;

public static partial class RevitFileDeduplicator
{
    [GeneratedRegex(@"\d{2,}")]
    private static partial Regex RvtNumberPattern();

    /// <summary>
    /// Removes duplicate Revit files by exact file name first, then by matching numeric tokens within a prefix group.
    /// The shortest name wins inside each group; equal-length names keep their original order.
    /// </summary>
    public static List<string> Deduplicate(IReadOnlyCollection<string> files)
    {
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<RevitFileCandidate>(files.Count);

        foreach (var path in files)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!seenNames.Add(name))
            {
                continue;
            }

            candidates.Add(new RevitFileCandidate(path, name, ExtractNumbers(name)));
        }

        var result = new List<string>(candidates.Count);

        foreach (var group in candidates.GroupBy(
            candidate => candidate.Name.Length > 15 ? candidate.Name[..15] : candidate.Name,
            StringComparer.OrdinalIgnoreCase))
        {
            var accepted = new List<RevitFileCandidate>();

            foreach (var candidate in group.OrderBy(candidate => candidate.Name.Length))
            {
                var isDuplicate = false;
                foreach (var acceptedCandidate in accepted)
                {
                    if (acceptedCandidate.Numbers.Overlaps(candidate.Numbers))
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

    private sealed record RevitFileCandidate(string Path, string Name, HashSet<long> Numbers);
}
