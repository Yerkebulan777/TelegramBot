using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Server.Services.Application;

namespace TelegramBot.Server.Services.Infrastructure.FileSystem;

public class FileSystemBrowser(SessionManager sessions, IOptions<FileSystemOptions> options, ILogger<FileSystemBrowser> logger)
{
    private static readonly TimeSpan DirectoryCacheTtl = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, CacheEntry<string[]>> _directoryCache = new();
    private readonly ConcurrentDictionary<string, CacheEntry<List<string>>> _fileCache = new();

    private readonly FileSystemOptions _options = options.Value;
    private readonly Regex _folderRegex = new(options.Value.SectionFolderPattern, RegexOptions.IgnoreCase);

    private static readonly HashSet<string> _sectionAcronyms = new(StringComparer.OrdinalIgnoreCase)
    {
        "AR", "AS", "APT", "KJ", "KR", "KG", "OV", "VK", "EOM", "EM", "PS", "SS", "OViK"
    };

    private static readonly char[] _nameSeparators = ['_', '-', ' ', '.'];

    private const long RvtMinFileSizeBytes = 50L * 1024 * 1024;

    private static readonly Regex _rvtSectionPattern =
        new(@"(?:^|[_ -])[BSCPKITGM]+\d*[_ -][ASRPGJOVIK]+\d*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly EnumerationOptions _rvtEnumOptions = new()
    {
        RecurseSubdirectories = true,
        MaxRecursionDepth = 3,
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private readonly record struct CacheEntry<T>(T Value, DateTime ExpiresAt);

    private static T GetOrCache<T>(ConcurrentDictionary<string, CacheEntry<T>> cache, string key, Func<T> factory)
    {
        var now = DateTime.UtcNow;

        if (cache.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Value;
        }

        var value = factory();
        cache[key] = new CacheEntry<T>(value, now.Add(DirectoryCacheTtl));
        return value;
    }

    private string[] GetCachedDirectories(string path) =>
        GetOrCache(_directoryCache, path, () => Directory.GetDirectories(path));

    private List<string> GetCachedSectionFiles(string sectionPath) =>
        GetOrCache(_fileCache, sectionPath, () => ScanSectionFiles(sectionPath));

    public InlineKeyboardMarkup GetSectionsView(long userId, string path)
    {
        var session = sessions.GetOrCreateSession(userId);

        if (IsSectionFileLevel(path))
        {
            return BuildFilesKeyboard(session, path);
        }

        return IsSectionLevel(path)
            ? BuildSectionKeyboard(session, path)
            : BuildProjectKeyboard(session, path);
    }

    /// <summary>Файлы для кнопки "Выбрать все": доступна только на уровне файлов одного раздела.</summary>
    public List<string> GetSelectableFiles(string path)
    {
        return GetCachedSectionFiles(path);
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

        IEnumerable<string> candidates = IsSectionFileLevel(currentPath)
            ? GetCachedSectionFiles(currentPath)
            : IsSectionLevel(currentPath)
                ? EnumerateSectionFolders(currentPath)
                : EnumerateProjectFolders(currentPath);

        return candidates.FirstOrDefault(candidate =>
            string.Equals(CreateSelectionToken(candidate), callbackArgument, StringComparison.OrdinalIgnoreCase));
    }

    private InlineKeyboardMarkup BuildProjectKeyboard(UserSession session, string path)
    {
        var selected = session.GetSelectedFiles();
        var buttons = new List<List<InlineKeyboardButton>>();

        foreach (var dir in EnumerateProjectFolders(path))
        {
            var label = $"{(selected.Contains(dir) ? "✅ " : "📁 ")}{Path.GetFileName(dir)}";
            buttons.Add([InlineKeyboardButton.WithCallbackData(label, $"{CallbackPrefixes.File}{CreateSelectionToken(dir)}")]);
        }

        return new InlineKeyboardMarkup(buttons);
    }

    private InlineKeyboardMarkup BuildSectionKeyboard(UserSession session, string path)
    {
        var selected = session.GetSelectedFiles();
        var buttons = new List<List<InlineKeyboardButton>>();

        foreach (var dir in EnumerateSectionFolders(path))
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
        var buttons = new List<List<InlineKeyboardButton>>
        {
            new() { InlineKeyboardButton.WithCallbackData("⬅️ Назад", CallbackPrefixes.OpenFolder) }
        };

        foreach (var file in GetCachedSectionFiles(path))
        {
            var label = $"{(selected.Contains(file) ? "✅ " : "📄 ")}{Path.GetFileName(file)}";
            buttons.Add([InlineKeyboardButton.WithCallbackData(label, $"{CallbackPrefixes.File}{CreateSelectionToken(file)}")]);
        }

        buttons.Add([InlineKeyboardButton.WithCallbackData("Выбрать все", CallbackPrefixes.SelectAllSectionFolders)]);

        return new InlineKeyboardMarkup(buttons);
    }

    private bool IsSectionLevel(string path)
    {
        return string.Equals(
            Path.GetFileName(path), _options.ProjectDirectoryName,
            StringComparison.OrdinalIgnoreCase);
    }

    private bool IsSectionFileLevel(string path)
    {
        var parent = Path.GetDirectoryName(path);
        return parent != null && IsSectionLevel(parent);
    }

    private static string CreateSelectionToken(string path)
    {
        var normalizedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath));

        return Convert.ToHexString(hash)[..16];
    }

    private IEnumerable<string> EnumerateProjectFolders(string path)
    {
        foreach (var dir in GetCachedDirectories(path))
        {
            var name = Path.GetFileName(dir)!;

            if (_folderRegex.IsMatch(name) && Directory.Exists(Path.Combine(dir, _options.ProjectDirectoryName)))
            {
                yield return dir;
            }
        }
    }

    private IEnumerable<string> EnumerateSectionFolders(string path)
    {
        return GetCachedDirectories(path).Where(dir => ContainsSectionAcronym(Path.GetFileName(dir)));
    }

    private static bool ContainsSectionAcronym(string folderName)
    {
        var nameSegments = folderName.Split(_nameSeparators, StringSplitOptions.RemoveEmptyEntries);
        return nameSegments.Any(_sectionAcronyms.Contains);
    }

    private List<string> ScanSectionFiles(string sectionPath)
    {
        var rvtDir = _options.GetRvtPath(sectionPath);
        if (!Directory.Exists(rvtDir))
        {
            return [];
        }

        try
        {
            var files = new DirectoryInfo(rvtDir)
                .EnumerateFiles("*.rvt", _rvtEnumOptions)
                .Where(IsValidRevitFile)
                .Select(fi => (fi.FullName, Depth: GetDepth(rvtDir, fi.DirectoryName!)))
                .ToList();

            return RevitFileDeduplicator.Deduplicate(files);
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

    private static int GetDepth(string rvtDir, string fileDir)
    {
        var relative = Path.GetRelativePath(rvtDir, fileDir);
        return relative == "." ? 0 : relative.Count(c => c is '\\' or '/') + 1;
    }

    private bool IsValidRevitFile(FileInfo fi)
    {
        var name = Path.GetFileNameWithoutExtension(fi.Name);

        if (name.Length is <10 or >50)
        {
            return false;
        }

        if (name.EndsWith("отсоединено", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!_rvtSectionPattern.IsMatch(name))
        {
            return false;
        }

        try
        {
            return fi.Length > RvtMinFileSizeBytes;
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Failed to read file size for {FilePath}", fi.FullName);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "Access denied reading file size for {FilePath}", fi.FullName);
            return false;
        }
    }
}
