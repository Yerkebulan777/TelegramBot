using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;
using TelegramBot.Server.Services.Application;

namespace TelegramBot.Server.Services.Infrastructure.FileSystem;

public class FileSystemBrowser(SessionManager sessions, IOptions<FileSystemOptions> options)
{
    private static readonly TimeSpan DirectoryCacheTtl = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, CachedDirectoryListing> _directoryCache = new();

    private readonly FileSystemOptions _options = options.Value;
    private readonly Regex _folderRegex = new(options.Value.SectionFolderPattern, RegexOptions.IgnoreCase);

    private static readonly HashSet<string> _sectionAcronyms = new(StringComparer.OrdinalIgnoreCase)
    {
        "AR", "AS", "APT", "KJ", "KR", "KG", "OV", "VK", "EOM", "EM", "PS", "SS", "OViK"
    };

    private static readonly char[] _nameSeparators = ['_', '-', ' ', '.'];

    private readonly record struct CachedDirectoryListing(string[] Paths, DateTime ExpiresAt);

    private string[] GetCachedDirectories(string path)
    {
        var now = DateTime.UtcNow;

        if (_directoryCache.TryGetValue(path, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Paths;
        }

        var paths = Directory.GetDirectories(path);
        _directoryCache[path] = new CachedDirectoryListing(paths, now.Add(DirectoryCacheTtl));
        return paths;
    }

    public Task<InlineKeyboardMarkup> GetSectionsViewAsync(long userId, string path)
    {
        var session = sessions.GetOrCreateSession(userId);

        var atSectionLevel = string.Equals(
            Path.GetFileName(path), _options.ProjectDirectoryName,
            StringComparison.OrdinalIgnoreCase);

        var keyboard = atSectionLevel
            ? BuildSectionKeyboard(session, path)
            : BuildProjectKeyboard(session, path);

        return Task.FromResult(keyboard);
    }

    public List<string> GetSectionFolderPaths(string path)
    {
        return EnumerateSectionFolders(path).ToList();
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

        var folders = IsSectionLevel(currentPath)
            ? EnumerateSectionFolders(currentPath)
            : EnumerateProjectFolders(currentPath);

        return folders.FirstOrDefault(folder =>
            string.Equals(CreateSelectionToken(folder), callbackArgument, StringComparison.OrdinalIgnoreCase));
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
            var label = $"{(selected.Contains(dir) ? "✅ " : "📁 ")}{Path.GetFileName(dir)}";
            buttons.Add([InlineKeyboardButton.WithCallbackData(label, $"{CallbackPrefixes.File}{CreateSelectionToken(dir)}")]);
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

}
