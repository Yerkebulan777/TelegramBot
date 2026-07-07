using System.Globalization;
using System.Text.RegularExpressions;

namespace TelegramBot.Server.Services.Application;

/// <summary>
/// Удаление дубликатов RVT-файлов: exact-name dedup + grouping по первым 15 символам + numeric-token overlap.
/// Все файлы передаются на одном уровне вложенности, поэтому глубина не учитывается.
/// Алгоритм: O(n log n) за счёт сортировки вместо O(n²) вложенных циклов.
/// </summary>
public static partial class RevitFileDeduplicator
{
    [GeneratedRegex(@"\d{2,}")]
    private static partial Regex RvtNumberPattern();

    private const int _prefixLength = 15;
    private const int _nameLengthTolerance = 5;

    /// <summary>
    /// Singleton для файлов без numeric tokens: avoids per-call allocation.
    /// Безопасно, т.к. после <see cref="ExtractNumbers"/> множество только читается.
    /// </summary>
    private static readonly HashSet<long> _emptyNumbers = new();

    /// <summary>
    /// Удаляет дубликаты файлов:
    /// 1. Exact-name dedup (при совпадении имени выигрывает более короткий путь)
    /// 2. Grouping по первым 15 символам
    /// 3. Внутри группы: numeric-token overlap filtering (короткое имя выигрывает)
    /// </summary>
    public static List<string> Deduplicate(IReadOnlyCollection<string> files)
    {
        if (files.Count == 0)
        {
            return [];
        }

        // Шаг 1: Exact-name dedup — O(n). При одинаковых именах выигрывает более короткий путь.
        var byName = new Dictionary<string, string>(files.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!byName.TryGetValue(name, out var existing) || path.Length < existing.Length)
            {
                byName[name] = path;
            }
        }

        // Шаги 2-3 совмещены: кандидаты сразу группируются по префиксу в Dictionary,
        // без промежуточной LINQ-коллекции (candidates.Select().ToList() + GroupBy).
        var groups = new Dictionary<string, List<RevitFileCandidate>>(byName.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, path) in byName)
        {
            var candidate = new RevitFileCandidate(path, name, ExtractNumbers(name));
            var prefix = name.Length > _prefixLength ? name[.._prefixLength] : name;
            if (!groups.TryGetValue(prefix, out var bucket))
            {
                bucket = new List<RevitFileCandidate>();
                groups[prefix] = bucket;
            }
            bucket.Add(candidate);
        }

        var result = new List<string>(byName.Count);
        foreach (var bucket in groups.Values)
        {
            // Одиночный кандидат — overlap-фильтрация ничего не даст, пропускаем аллокацию accepted-списка.
            if (bucket.Count == 1)
            {
                result.Add(bucket[0].Path);
                continue;
            }

            // In-place Sort быстрее OrderBy().ToList(): нет промежуточного buffer + enumerator.
            // Tie-breaker по Path даёт детерминизм (List.Sort нестабилен, в отличие от OrderBy).
            bucket.Sort(NameLengthComparison);

            // Шаг 4: Overlap filtering с прямым добавлением в result — без финального LINQ Select.
            FilterOverlappingCandidates(bucket, result);
        }

        return result;
    }

    private static int NameLengthComparison(RevitFileCandidate a, RevitFileCandidate b)
    {
        var c = a.Name.Length.CompareTo(b.Name.Length);
        return c != 0 ? c : string.CompareOrdinal(a.Path, b.Path);
    }

    /// <summary>
    /// Фильтрует кандидатов с overlapping numeric tokens и сразу добавляет принятые в <paramref name="result"/>.
    /// Оптимизация: проверяем только с уже принятыми, а не со всеми парами; без отдельного accepted-списка
    /// и финальной LINQ-проекции.
    /// </summary>
    private static void FilterOverlappingCandidates(List<RevitFileCandidate> sortedCandidates, List<string> result)
    {
        // accepted хранит только кандидаты с непустым numeric set — для файлов без чисел overlap невозможен,
        // и их не нужно проверять в качестве "existing".
        var accepted = new List<RevitFileCandidate>(sortedCandidates.Count);
        foreach (var candidate in sortedCandidates)
        {
            var isDuplicate = false;
            foreach (var acceptedCandidate in accepted)
            {
                // Длина имени отличается ≤5 + есть общие numeric tokens.
                // Оба множества непустые (accepted — по построению), Overlaps корректен.
                if (Math.Abs(acceptedCandidate.Name.Length - candidate.Name.Length) <= _nameLengthTolerance
                    && acceptedCandidate.Numbers.Overlaps(candidate.Numbers))
                {
                    isDuplicate = true;
                    break;
                }
            }

            if (!isDuplicate)
            {
                result.Add(candidate.Path);
                // Только кандидаты с числами могут стать "existing" для последующих overlap-проверок.
                if (candidate.Numbers.Count > 0)
                {
                    accepted.Add(candidate);
                }
            }
        }
    }

    private static HashSet<long> ExtractNumbers(string name)
    {
        HashSet<long>? result = null;
        foreach (var match in RvtNumberPattern().EnumerateMatches(name))
        {
            // Zero-allocation: парсим digit-span прямо из исходной строки через span.
            // NumberStyles.None + InvariantCulture => только ASCII-цифры, без влияния культуры.
            if (long.TryParse(
                name.AsSpan(match.Index, match.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value))
            {
                (result ??= new HashSet<long>()).Add(value);
            }
        }

        return result ?? _emptyNumbers;
    }

    private sealed record RevitFileCandidate(string Path, string Name, HashSet<long> Numbers);
}
