using Microsoft.Extensions.Options;
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

public sealed partial class FileSystemBrowser(IOptions<FileSystemOptions> options, ILogger<FileSystemBrowser> logger)
{
    private readonly FileSystemOptions _options = options.Value;
    private readonly Regex _folderRegex = new(options.Value.SectionFolderPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [GeneratedRegex(@"(?:^|[_ -])[BSCPKITGM]+\d*[_ -][ASRPGJOVIK]+\d*", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ru-RU")]
    private static partial Regex ValidRvtFilePattern();

    [GeneratedRegex(@"\.\d{3,5}$", RegexOptions.Compiled)]
    private static partial Regex RevitBackupFilePattern();

    private static readonly HashSet<string> _sectionAcronyms = new(StringComparer.OrdinalIgnoreCase)
    {
        "AR", "AS", "APT", "KJ", "KR", "KG", "OV", "VK", "EOM", "EM", "PS", "SS", "OViK"
    };

    private const long _rvtMinFileSizeBytes = 50L * 1024 * 1024;
    private const int _projectButtonNameLength = 50;

    private static readonly Regex _rvtSectionPattern = ValidRvtFilePattern();
    private static readonly Regex _rvtBackupFilePattern = RevitBackupFilePattern();

    private static readonly EnumerationOptions _enumOptions = new()
    {
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public InlineKeyboardMarkup GetSectionsView(UserSession session, string path)
    {
        return SelectionFlow.GetLevel(path, session.RootPath, _options.ProjectDirectoryName) switch
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

    public string? ResolveSelectionPath(string rootPath, string currentPath, string callbackArgument)
    {
        if (string.IsNullOrWhiteSpace(callbackArgument))
        {
            return null;
        }

        if (FileSystemOptions.IsPathWithinRoot(rootPath, callbackArgument))
        {
            return callbackArgument;
        }

        var candidates = SelectionFlow.GetLevel(currentPath, rootPath, _options.ProjectDirectoryName) switch
        {
            SelectionFlow.Level.Files => GetFilesLevelCandidates(currentPath),
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
            var label = $"📁 {FormatProjectButtonName(Path.GetFileName(dir))}";
            buttons.Add([InlineKeyboardButton.WithCallbackData(label, $"{CallbackPrefixes.OpenFolder}{CreateSelectionToken(dir)}")]);
        }

        return new InlineKeyboardMarkup(buttons);
    }

    private static string FormatProjectButtonName(string name)
    {
        const string suffix = "...";
        if (name.Length <= _projectButtonNameLength)
        {
            return name.PadRight(_projectButtonNameLength);
        }

        var prefixLength = _projectButtonNameLength - suffix.Length;
        if (char.IsHighSurrogate(name[prefixLength - 1]) && char.IsLowSurrogate(name[prefixLength]))
        {
            prefixLength--;
        }

        return name[..prefixLength].PadRight(_projectButtonNameLength - suffix.Length) + suffix;
    }

    private InlineKeyboardMarkup BuildSectionKeyboard(UserSession session, string path)
    {
        var selected = session.Selection.SelectedFiles;
        var buttons = new List<List<InlineKeyboardButton>>
        {
            new() { InlineKeyboardButton.WithCallbackData("⬅️ Назад", CallbackPrefixes.OpenFolder) }
        };

        // Add only section folders
        var folders = GetSectionFolders(path);
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
    /// Если в целевой RVT-директории есть валидные подпапки (без '#' и содержащие файлы .rvt) И файлы верхнего уровня,
    /// то отображаются кнопки подпапок (📁/✅) и кнопки файлов верхнего уровня (🔵/✅),
    /// иначе отображаются только файлы верхнего уровня или файлы из подпапки.
    /// </summary>
    private InlineKeyboardMarkup BuildFilesKeyboard(UserSession session, string path)
    {
        var selected = session.Selection.SelectedFiles;
        var candidates = GetFilesLevelCandidates(path);
        var buttons = new List<List<InlineKeyboardButton>>(candidates.Count + 3)
        {
            new() { InlineKeyboardButton.WithCallbackData("⬅️ Назад", CallbackPrefixes.OpenFolder) }
        };

        foreach (var candidate in candidates)
        {
            var isDirectory = Directory.Exists(candidate);
            if (isDirectory)
            {
                // It's a subfolder
                var prefix = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var hasSelection = selected.Any(file => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                var label = $"{(hasSelection ? "✅ " : "📁 ")}{Path.GetFileName(candidate)}";
                buttons.Add([InlineKeyboardButton.WithCallbackData(label, $"{CallbackPrefixes.OpenFolder}{CreateSelectionToken(candidate)}")]);
            }
            else
            {
                // It's a file
                var label = $"{(selected.Contains(candidate) ? "✅ " : "🔵 ")}{Path.GetFileName(candidate)}";
                buttons.Add([InlineKeyboardButton.WithCallbackData(label, $"{CallbackPrefixes.File}{CreateSelectionToken(candidate)}")]);
            }
        }

        buttons.Add([InlineKeyboardButton.WithCallbackData("Выбрать все", CallbackPrefixes.SelectAllSectionFolders)]);
        buttons.Add(
        [
            InlineKeyboardButton.WithCallbackData(ButtonTexts.Confirm, CallbackPrefixes.ConfirmFileSelection),
            InlineKeyboardButton.WithCallbackData(ButtonTexts.Cancel, CallbackPrefixes.CancelFileSelection)
        ]);

        return new InlineKeyboardMarkup(buttons);
    }

    /// <summary>SHA256 от нормализованного пути; не зависит от содержимого ФС.</summary>
    private static string CreateSelectionToken(string path)
    {
        var normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..16];
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
                if (folderName.Contains('#'))
                {
                    continue;
                }
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
            return [.. Directory.GetDirectories(path, "*", _enumOptions).Where(dir => !Path.GetFileName(dir.AsSpan()).Contains('#') && ContainsSectionAcronym(Path.GetFileName(dir)))];
        }
        else
        {
            return [];
        }
    }

    /// <summary>
    /// Получает кандидатов на уровне Files: субпапки 01_RVT и файлы верхнего уровня.
    /// Если мы находимся в подпапке 01_RVT, возвращает файлы из этой подпапки.
    /// </summary>
    private List<string> GetFilesLevelCandidates(string currentPath)
    {
        var result = new List<string>();

        // Check if currentPath is inside a subfolder of 01_RVT
        var parent = Path.GetDirectoryName(currentPath);
        var parentDir = parent != null ? Path.GetFileName(parent) : null;

        if (parent != null && string.Equals(parentDir, _options.RvtDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            // We're inside a subfolder of 01_RVT, return only files from this subfolder
            result.AddRange(GetSectionFiles(currentPath));
        }
        else
        {
            // We're at the section level, add valid subfolders and ONLY top-level files
            var subfolders = GetValidSubfolders(currentPath);
            result.AddRange(subfolders);

            var rvtDir = Path.Combine(currentPath, _options.RvtDirectoryName);
            if (Directory.Exists(rvtDir))
            {
                var topLevelFiles = RevitFileDeduplicator.Deduplicate(CollectRevitFiles(new DirectoryInfo(rvtDir)));
                result.AddRange(topLevelFiles);
            }
        }

        return result;
    }

    /// <summary>
    /// Получает подпапки в 01_RVT, которые не содержат '#' и имеют .rvt файлы.
    /// </summary>
    private List<string> GetValidSubfolders(string sectionPath)
    {
        var rvtDir = Path.Combine(sectionPath, _options.RvtDirectoryName);

        if (!Directory.Exists(rvtDir))
        {
            return [];
        }

        try
        {
            var root = new DirectoryInfo(rvtDir);
            return [.. root.EnumerateDirectories("*", _enumOptions)
                .Where(s => !s.Name.Contains('#') && CollectRevitFiles(s).Count > 0)
                .Select(s => s.FullName)];
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Scan RVT subfolders fail: {RvtDir}", rvtDir);
            return [];
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "Access denied: {RvtDir}", rvtDir);
            return [];
        }
    }



    /// <summary>
    /// Дедуплицированные .rvt-файлы раздела.
    /// Стратегия (максимальная производительность — без глубокого рекурсивного скана):
    /// 1. Если мы находимся в подпапке 01_RVT, берём файлы из неё.
    /// 2. Иначе (находимся в разделе), берём файлы верхнего уровня 01_RVT.
    ///    Если они есть — возвращаются они И файлы из всех валидных подпапок (дедуплицированные вместе).
    /// 3. Если файлов верхнего уровня нет — возвращаются файлы из всех валидных подпапок.
    /// </summary>
    private List<string> GetSectionFiles(string sectionPath)
    {
        // Check if sectionPath is actually a subfolder inside 01_RVT
        var parentDir = Path.GetDirectoryName(sectionPath);
        if (parentDir != null && string.Equals(Path.GetFileName(parentDir), _options.RvtDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            // We're inside a subfolder of 01_RVT, return files from this subfolder
            try
            {
                var subfolderInfo = new DirectoryInfo(sectionPath);
                var files = CollectRevitFiles(subfolderInfo);
                return RevitFileDeduplicator.Deduplicate(files);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Scan RVT subfolder fail: {SubfolderPath}", sectionPath);
                return [];
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(ex, "Access denied: {SubfolderPath}", sectionPath);
                return [];
            }
        }

        // Normal case: sectionPath is a section folder containing 01_RVT
        var rvtDir = Path.Combine(sectionPath, _options.RvtDirectoryName);

        if (Directory.Exists(rvtDir))
        {
            try
            {
                var root = new DirectoryInfo(rvtDir);
                var topLevel = CollectRevitFiles(root);
                var allFiles = new List<string>(topLevel);

                // Always add files from all valid subfolders
                var validSubfolders = GetValidSubfolders(sectionPath);
                allFiles.AddRange(validSubfolders.SelectMany(s => CollectRevitFiles(new DirectoryInfo(s))));

                return RevitFileDeduplicator.Deduplicate(allFiles);
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
        if (dirInfo.Name.Contains('#'))
        {
            return [];
        }

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

