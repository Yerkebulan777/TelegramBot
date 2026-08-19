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
    private readonly FileSystemOptions _options = options.Value;
    private readonly Regex _folderRegex = new(options.Value.SectionFolderPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [GeneratedRegex(@"(?:^|[_ -])[BSCPKITGM]+\d*[_ -][ASRPGJOVIK]+\d*", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ru-RU")]
    private static partial Regex ValidRvtFilePattern();

    [GeneratedRegex(@"\.\d{3,5}$", RegexOptions.Compiled)]
    private static partial Regex RevitBackupFilePattern();

    // Токены выбора путей стабильны (SHA256 от нормализованного пути) и не зависят от содержимого ФС.
    private readonly ConcurrentDictionary<string, string> _tokenCache = new();

    private static readonly HashSet<string> _sectionAcronyms = new(StringComparer.OrdinalIgnoreCase)
    {
        "AR", "AS", "APT", "KJ", "KR", "KG", "OV", "VK", "EOM", "EM", "PS", "SS", "OViK"
    };

    private const long _rvtMinFileSizeBytes = 50L * 1024 * 1024;

    private static readonly Regex _rvtSectionPattern = ValidRvtFilePattern();
    private static readonly Regex _rvtBackupFilePattern = RevitBackupFilePattern();

    private static readonly EnumerationOptions _enumOptions = new()
    {
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public InlineKeyboardMarkup GetSectionsView(long userId, string path)
    {
        var session = sessions.GetOrCreateSession(userId);

        return SelectionFlow.GetLevel(path, _options.RootPath, _options.ProjectDirectoryName) switch
        {
            SelectionFlow.Level.Files => BuildFilesKeyboard(session, path),
            SelectionFlow.Level.Sections => BuildSectionKeyboard(session, path),
            _ => BuildProjectKeyboard(session, path)
        };
    }

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

        var candidates = SelectionFlow.GetLevel(currentPath, _options.RootPath, _options.ProjectDirectoryName) switch
        {
            SelectionFlow.Level.Files => GetSectionFiles(currentPath),
            SelectionFlow.Level.Sections => GetSectionFolders(currentPath),
            _ => GetProjectFolders(currentPath)
        };

        return candidates.FirstOrDefault(c => string.Equals(CreateSelectionToken(c), callbackArgument, StringComparison.OrdinalIgnoreCase));
    }

    private InlineKeyboardMarkup BuildProjectKeyboard(UserSession session, string path)
    {
        var folders = GetProjectFolders(path);
        var buttons = new List<List<InlineKeyboardButton>>(folders.Count);

        foreach (var dir in folders)
        {
            var label = $"📁 {Path.GetFileName(dir)}";
            buttons.Add([InlineKeyboardButton.WithCallbackData(label, $"{CallbackPrefixes.OpenFolder}{CreateSelectionToken(dir)}")]);
        }

        return new InlineKeyboardMarkup(buttons);
    }

    private InlineKeyboardMarkup BuildSectionKeyboard(UserSession session, string path)
    {
        var selected = session.Selection.SelectedFiles;
        var folders = GetSectionFolders(path);
        var buttons = new List<List<InlineKeyboardButton>>(folders.Count + 1)
        {
            new() { InlineKeyboardButton.WithCallbackData("⬅️ Назад", CallbackPrefixes.OpenFolder) }
        };

        foreach (var dir in folders)
        {
            var prefix = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var hasSelection = selected.Any(file => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            var label = $"{(hasSelection ? "✅ " : "📁 ")}{Path.GetFileName(dir)}";
            buttons.Add([InlineKeyboardButton.WithCallbackData(label, $"{CallbackPrefixes.OpenFolder}{CreateSelectionToken(dir)}")]);
        }

        return new InlineKeyboardMarkup(buttons);
    }

    /// <summary>
    /// Список файлов раздела: та же механика выбора (чекбоксы + "Выбрать все"), что и у списка разделов.
    /// </summary>
    private InlineKeyboardMarkup BuildFilesKeyboard(UserSession session, string path)
    {
        var selected = session.Selection.SelectedFiles;
        var files = GetSectionFiles(path);
        var buttons = new List<List<InlineKeyboardButton>>(files.Count + 2)
        {
            new() { InlineKeyboardButton.WithCallbackData("⬅️ Назад", CallbackPrefixes.OpenFolder) }
        };

        foreach (var file in files)
        {
            var label = $"{(selected.Contains(file) ? "✅ " : "🔵")}{Path.GetFileName(file)}";
            buttons.Add([InlineKeyboardButton.WithCallbackData(label, $"{CallbackPrefixes.File}{CreateSelectionToken(file)}")]);
        }

        buttons.Add([InlineKeyboardButton.WithCallbackData("Выбрать все", CallbackPrefixes.SelectAllSectionFolders)]);

        return new InlineKeyboardMarkup(buttons);
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

    /// <summary>
    /// Папки-проекты: совпадают с SectionFolderPattern и содержат ProjectDirectoryName.
    /// </summary>
    private List<string> GetProjectFolders(string path)
    {
        var result = new List<string>(100);

        if (Directory.Exists(path))
        {
            foreach (var dir in Directory.EnumerateDirectories(path, "*", _enumOptions))
            {
                var folderName = Path.GetFileName(dir.AsSpan());
                var projectDirPath = Path.Combine(dir, _options.ProjectDirectoryName);
                if (_folderRegex.IsMatch(folderName) && Directory.Exists(projectDirPath))
                {
                    result.Add(dir);
                }
            }
        }

        return result;
    }

    /// <summary>Папки разделов внутри ProjectDirectoryName, имя которых содержит известный acronym.</summary>
    private static List<string> GetSectionFolders(string path)
    {
        if (Directory.Exists(path))
        {
            return [.. Directory.GetDirectories(path, "*", _enumOptions).Where(dir => ContainsSectionAcronym(Path.GetFileName(dir)))];
        }
        else
        {
            return [];
        }
    }

    /// <summary>
    /// Дедуплицированные .rvt-файлы раздела.
    /// Стратегия (максимальная производительность — без глубокого рекурсивного скана):
    /// 1. Файлы верхнего уровня <c>01_RVT</c>. Если они есть — возвращаются только они.
    /// 2. Иначе — файлы в прямых субпапках <c>01_RVT</c>. Дальше не углубляемся.
    /// Единственный try/catch — safety net против race (каталог удалён между Exists и enumerate).
    /// </summary>
    private List<string> GetSectionFiles(string sectionPath)
    {
        var rvtDir = Path.Combine(sectionPath, _options.RvtDirectoryName);

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
                logger.LogWarning(ex, "Scan RVT dirInfo fail: {RvtDir}", rvtDir);
                return [];
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(ex, "Access denied: {RvtDir}", rvtDir);
                return [];
            }
        }

        return [];
    }

    private static bool ContainsSectionAcronym(string folderName)
    {
        var folderSpan = folderName.AsSpan();

        foreach (var acronym in _sectionAcronyms)
        {
            if (folderSpan.Length <= 10 && folderSpan.EndsWith(acronym, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> CollectRevitFiles(DirectoryInfo dirInfo)
    {
        var mandatoryOnly = new List<string>(10);
        var sectionMatched = new List<string>(10);

        foreach (var fileInfo in dirInfo.EnumerateFiles("*.rvt", _enumOptions))
        {
            var name = Path.GetFileNameWithoutExtension(fileInfo.Name);

            if (name.Length is < 10 or > 50)
            {
                continue;
            }
            if (_rvtBackupFilePattern.IsMatch(name))
            {
                continue;
            }
            if (fileInfo.Length < _rvtMinFileSizeBytes)
            {
                continue;
            }
            if (name.EndsWith("detached", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (name.EndsWith("отсоединено", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_rvtSectionPattern.IsMatch(name))
            {
                sectionMatched.Add(fileInfo.FullName);
            }

            mandatoryOnly.Add(fileInfo.FullName);
        }

        return sectionMatched.Count > 0 ? sectionMatched : mandatoryOnly;
    }
}
