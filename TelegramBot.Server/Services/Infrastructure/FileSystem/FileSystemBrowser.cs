using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Server.Helpers;
using TelegramBot.Server.Services.Application;

namespace TelegramBot.Server.Services.Infrastructure.FileSystem;

public sealed partial class FileSystemBrowser(SessionManager sessions, IOptions<FileSystemOptions> options, ILogger<FileSystemBrowser> logger)
{
    private static readonly TimeSpan _cacheTtl = TimeSpan.FromSeconds(5);

    private readonly FileSystemOptions _options = options.Value;
    private readonly Regex _folderRegex = new(options.Value.SectionFolderPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [GeneratedRegex(@"(?:^|[_ -])[BSCPKITGM]+\d*[_ -][ASRPGJOVIK]+\d*", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ru-RU")]
    private static partial Regex ValidRvtFilePattern();

    // Кэши хранят уже отфильтрованные результаты: I/O-проверки выполняются один раз за TTL,
    // а не на каждом рендере клавиатуры.
    private readonly ConcurrentDictionary<string, CacheEntry<List<string>>> _projectFolderCache = new();
    private readonly ConcurrentDictionary<string, CacheEntry<List<string>>> _sectionFolderCache = new();
    private readonly ConcurrentDictionary<string, CacheEntry<List<string>>> _sectionFileCache = new();
    // Токены выбора путей стабильны (SHA256 от нормализованного пути) — кэшируются без TTL.
    private readonly ConcurrentDictionary<string, string> _tokenCache = new();

    private static readonly HashSet<string> _sectionAcronyms = new(StringComparer.OrdinalIgnoreCase)
    {
        "AR", "AS", "APT", "KJ", "KR", "KG", "OV", "VK", "EOM", "EM", "PS", "SS", "OViK"
    };

    private static readonly char[] _nameSeparators = ['_', '-', ' ', '.'];

    private const long _rvtMinFileSizeBytes = 50L * 1024 * 1024;

    private static readonly Regex _rvtSectionPattern = ValidRvtFilePattern();

    private static readonly EnumerationOptions _enumOptions = new()
    {
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private readonly record struct CacheEntry<T>(T Value, DateTime ExpiresAt);

    private static List<string> GetOrCache(ConcurrentDictionary<string, CacheEntry<List<string>>> cache, string key, Func<List<string>> factory)
    {
        var now = DateTime.UtcNow;

        if (cache.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Value;
        }

        var value = factory();
        cache[key] = new CacheEntry<List<string>>(value, now.Add(_cacheTtl));
        return value;
    }

    public InlineKeyboardMarkup GetSectionsView(long userId, string path)
    {
        var session = sessions.GetOrCreateSession(userId);

        if (IsSectionFileLevel(path))
        {
            return BuildFilesKeyboard(session, path);
        }

        return IsSectionLevel(path) ? BuildSectionKeyboard(session, path) : BuildProjectKeyboard(session, path);
    }

    /// <summary>Файлы для кнопки "Выбрать все": доступна только на уровне файлов одного раздела.</summary>
    public List<string> GetSelectableFiles(string path)
    {
        return GetSectionFiles(path);
    }

    public string? ResolveSelectionPath(string currentPath, string callbackArgument)
    {
        if (string.IsNullOrWhiteSpace(callbackArgument))
        {
            return null;
        }

        if (_options.IsPathWithinRoot(callbackArgument))
        {
            return callbackArgument;
        }

        List<string> candidates;
        if (IsSectionFileLevel(currentPath))
        {
            candidates = GetSectionFiles(currentPath);
        }
        else
        {
            candidates =IsSectionLevel(currentPath) ? GetSectionFolders(currentPath) : GetProjectFolders(currentPath);
        }

        return candidates.FirstOrDefault(c => string.Equals(CreateSelectionToken(c), callbackArgument, StringComparison.OrdinalIgnoreCase));
    }

    private InlineKeyboardMarkup BuildProjectKeyboard(UserSession session, string path)
    {
        var selected = session.GetSelectedFiles();
        var folders = GetProjectFolders(path);
        var buttons = new List<List<InlineKeyboardButton>>(folders.Count);

        foreach (var dir in folders)
        {
            var label = $"{(selected.Contains(dir) ? "✅ " : "📁 ")}{Path.GetFileName(dir)}";
            buttons.Add([InlineKeyboardButton.WithCallbackData(label, $"{CallbackPrefixes.File}{CreateSelectionToken(dir)}")]);
        }

        return new InlineKeyboardMarkup(buttons);
    }

    private InlineKeyboardMarkup BuildSectionKeyboard(UserSession session, string path)
    {
        var selected = session.GetSelectedFiles();
        var folders = GetSectionFolders(path);
        var buttons = new List<List<InlineKeyboardButton>>(folders.Count);

        foreach (var dir in folders)
        {
            var prefix = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var hasSelection = selected.Any(file => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            var label = $"{(hasSelection ? "✅ " : "📁 ")}{Path.GetFileName(dir)}";
            buttons.Add([InlineKeyboardButton.WithCallbackData(label, $"{CallbackPrefixes.OpenFolder}{CreateSelectionToken(dir)}")]);
        }

        return new InlineKeyboardMarkup(buttons);
    }

    /// <summary>Список файлов раздела: та же механика выбора (чекбоксы + "Выбрать все"), что и у списка разделов.</summary>
    private InlineKeyboardMarkup BuildFilesKeyboard(UserSession session, string path)
    {
        var selected = session.GetSelectedFiles();
        var files = GetSectionFiles(path);
        var buttons = new List<List<InlineKeyboardButton>>(files.Count + 2)
        {
            new() { InlineKeyboardButton.WithCallbackData("⬅️ Назад", CallbackPrefixes.OpenFolder) }
        };

        foreach (var file in files)
        {
            var label = $"{(selected.Contains(file) ? "✅ " : "📄 ")}{Path.GetFileName(file)}";
            buttons.Add([InlineKeyboardButton.WithCallbackData(label, $"{CallbackPrefixes.File}{CreateSelectionToken(file)}")]);
        }

        buttons.Add([InlineKeyboardButton.WithCallbackData("Выбрать все", CallbackPrefixes.SelectAllSectionFolders)]);

        return new InlineKeyboardMarkup(buttons);
    }

    private bool IsSectionLevel(string path)
    {
        return string.Equals(Path.GetFileName(path), _options.ProjectDirectoryName, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsSectionFileLevel(string path)
    {
        var parent = Path.GetDirectoryName(path);
        return parent != null && IsSectionLevel(parent);
    }

    private string CreateSelectionToken(string path)
    {
        return _tokenCache.GetOrAdd(path, static p =>
        {
            var normalized = Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            return Convert.ToHexString(hash)[..16];
        });
    }

    /// <summary>Папки-проекты: совпадают с SectionFolderPattern и содержат ProjectDirectoryName.</summary>
    private List<string> GetProjectFolders(string path)
    {
        return GetOrCache(_projectFolderCache, path, () =>
        {
            if (!Directory.Exists(path))
            {
                return [];
            }

            return [.. Directory.GetDirectories(path, "*", _enumOptions)
                .Where(dir =>
                {
                    var name = Path.GetFileName(dir);
                    return _folderRegex.IsMatch(name) && Directory.Exists(Path.Combine(dir, _options.ProjectDirectoryName));
                })];
        });
    }

    /// <summary>Папки разделов внутри ProjectDirectoryName, имя которых содержит известный acronym.</summary>
    private List<string> GetSectionFolders(string path)
    {
        return GetOrCache(_sectionFolderCache, path, () =>
        {
            if (!Directory.Exists(path))
            {
                return [];
            }

            return Directory.GetDirectories(path, "*", _enumOptions)
                .Where(dir => ContainsSectionAcronym(Path.GetFileName(dir)))
                .ToList();
        });
    }

    /// <summary>Дедуплицированные .rvt-файлы раздела (результат кэшируется).</summary>
    private List<string> GetSectionFiles(string sectionPath)
    {
        return GetOrCache(_sectionFileCache, sectionPath, () => ScanSectionFiles(sectionPath));
    }

    private static bool ContainsSectionAcronym(string folderName)
    {
        var nameSegments = folderName.Split(_nameSeparators, StringSplitOptions.RemoveEmptyEntries);
        return nameSegments.Any(_sectionAcronyms.Contains);
    }

    /// <summary>
    /// Синхронный быстрый поиск RVT-файлов раздела.
    /// Стратегия (максимальная производительность — без глубокого рекурсивного скана):
    /// 1. Файлы верхнего уровня <c>01_RVT</c>. Если они есть — возвращаются только они.
    /// 2. Иначе — файлы в прямых субпапках <c>01_RVT</c>. Дальше не углубляемся.
    /// Единственный try/catch — safety net против race (каталог удалён между Exists и enumerate).
    /// </summary>
    private List<string> ScanSectionFiles(string sectionPath)
    {
        var rvtDir = _options.GetRvtPath(sectionPath);

        if (Directory.Exists(rvtDir))
        {
            try
            {
                var root = new DirectoryInfo(rvtDir);
                var topLevel = CollectRevitFiles(root);
                if (topLevel.Count > 0)
                {
                    return RevitFileDeduplicator.Deduplicate(topLevel);
                }

                // Top-level пустой — сканируем прямые субпапки, без дальнейшей рекурсии.
                var nested = root.EnumerateDirectories("*", _enumOptions)
                    .SelectMany(CollectRevitFiles)
                    .ToList();

                return RevitFileDeduplicator.Deduplicate(nested);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Failed to scan RVT directory {RvtDir}", rvtDir);
                return [];
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(ex, "Access denied scanning RVT directory {RvtDir}", rvtDir);
                return [];
            }
        }

        return [];
    }

    private static List<string> CollectRevitFiles(DirectoryInfo dir)
    {
        return [.. dir.EnumerateFiles("*.rvt", _enumOptions).Where(IsValidRevitFile).Select(fi => fi.FullName)];
    }

    private static bool IsValidRevitFile(FileInfo fi)
    {
        var name = Path.GetFileNameWithoutExtension(fi.Name);

        return name.Length is >= 10 and <= 50
            && !name.EndsWith("отсоединено", StringComparison.OrdinalIgnoreCase)
            && _rvtSectionPattern.IsMatch(name)
            && fi.Length > _rvtMinFileSizeBytes;
    }



}
