using System.Text.RegularExpressions;

namespace TelegramBot.Server.Services.Application;

/// <summary>
/// Удаление дубликатов RVT-файлов: exact-name dedup + grouping по первым 15 символам + numeric-token overlap.
/// Алгоритм: O(n log n) за счёт сортировки вместо O(n²) вложенных циклов.
/// </summary>
public static partial class RevitFileDeduplicator
{
    [GeneratedRegex(@"\d{2,}")]
    private static partial Regex RvtNumberPattern();

    /// <summary>
    /// Удаляет дубликаты файлов:
    /// 1. Exact-name dedup (ближе к корню выигрывает)
    /// 2. Grouping по первым 15 символам
    /// 3. Внутри группы: numeric-token overlap filtering (короткое имя выигрывает)
    /// </summary>
    public static List<string> Deduplicate(IReadOnlyCollection<(string Path, int Depth)> files)
    {
        if (files.Count == 0)
        {
            return [];
        }

        // Шаг 1: Exact-name dedup — O(n)
        var byName = new Dictionary<string, (string Path, int Depth)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, depth) in files)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!byName.TryGetValue(name, out var existing) || depth < existing.Depth)
            {
                byName[name] = (path, depth);
            }
        }

        // Шаг 2: Создание кандидатов и извлечение чисел — O(n * m), где m = среднее число токенов
        var candidates = byName
            .Select(entry => new RevitFileCandidate(entry.Value.Path, entry.Key, entry.Value.Depth, ExtractNumbers(entry.Key)))
            .ToList();

        // Шаг 3: Группировка по префиксу (15 символов) — O(n log n) из-за GroupBy
        var result = new List<string>(candidates.Count);
        foreach (var group in candidates.GroupBy(
            c => c.Name.Length > 15 ? c.Name[..15] : c.Name,
            StringComparer.OrdinalIgnoreCase))
        {
            // Сортировка внутри группы: depth asc, затем name length asc — O(k log k)
            var sorted = group
                .OrderBy(c => c.Depth)
                .ThenBy(c => c.Name.Length)
                .ToList();

            // Шаг 4: Overlap filtering — оптимизировано через precomputed number sets
            var accepted = FilterOverlappingCandidates(sorted);
            result.AddRange(accepted.Select(c => c.Path));
        }

        return result;
    }

    /// <summary>
    /// Фильтрует кандидатов с overlapping numeric tokens.
    /// Оптимизация: проверяем только с уже принятыми, а не со всеми парами.
    /// </summary>
    private static List<RevitFileCandidate> FilterOverlappingCandidates(List<RevitFileCandidate> sortedCandidates)
    {
        var accepted = new List<RevitFileCandidate>(sortedCandidates.Count);

        foreach (var candidate in sortedCandidates)
        {
            var isDuplicate = false;
            foreach (var acceptedCandidate in accepted)
            {
                // Длина имени отличается ≤5 + есть общие numeric tokens
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

        return accepted;
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
