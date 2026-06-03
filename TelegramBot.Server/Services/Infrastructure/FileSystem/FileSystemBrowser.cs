using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Config;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Infrastructure.FileSystem;

public class FileSystemBrowser : IFileSystemBrowser
{
    private readonly ISessionManager _sessions;
    private readonly FileSystemOptions _options;
    private readonly Regex _folderRegex;

    public FileSystemBrowser(ISessionManager sessions, IOptions<FileSystemOptions> options)
    {
        _sessions = sessions;
        _options = options.Value;
        _folderRegex = new Regex(_options.SectionFolderPattern, RegexOptions.IgnoreCase);
    }

    public bool TryResolvePath(long userId, string token, out string? path)
    {
        return _sessions.GetOrCreateSession(userId).PathMap.TryGetValue(token, out path);
    }

    public Task<InlineKeyboardMarkup> GetSectionsViewAsync(long userId, string path)
    {
        var session = _sessions.GetOrCreateSession(userId);
        session.PathMap.Clear();

        bool atSectionLevel = string.Equals(
            Path.GetFileName(path), _options.ProjectDirectoryName,
            StringComparison.OrdinalIgnoreCase);

        var keyboard = atSectionLevel
            ? BuildSectionKeyboard(session, path)
            : BuildProjectKeyboard(session, path);

        return Task.FromResult(keyboard);
    }

    private InlineKeyboardMarkup BuildProjectKeyboard(UserSession session, string path)
    {
        var selected = session.SelectedFiles;
        var buttons = new List<List<InlineKeyboardButton>>();

        foreach (var dir in EnumerateProjectFolders(path))
        {
            string token = NewToken();
            session.PathMap[token] = dir;
            string label = $"{(selected.Contains(dir) ? "✅ " : "📁 ")}{Path.GetFileName(dir)}";
            buttons.Add([InlineKeyboardButton.WithCallbackData(label, $"{CallbackPrefixes.File}{token}")]);
        }

        return new InlineKeyboardMarkup(buttons);
    }

    private InlineKeyboardMarkup BuildSectionKeyboard(UserSession session, string path)
    {
        var selected = session.SelectedFiles;
        var buttons = new List<List<InlineKeyboardButton>>();

        foreach (var dir in EnumerateSectionFolders(path))
        {
            string token = NewToken();
            session.PathMap[token] = dir;
            string label = $"{(selected.Contains(dir) ? "✅ " : "📁 ")}{Path.GetFileName(dir)}";
            buttons.Add([InlineKeyboardButton.WithCallbackData(label, $"{CallbackPrefixes.File}{token}")]);
        }

        return new InlineKeyboardMarkup(buttons);
    }

    private IEnumerable<string> EnumerateProjectFolders(string path)
    {
        foreach (var dir in Directory.GetDirectories(path))
        {
            var name = Path.GetFileName(dir)!;
            if (_folderRegex.IsMatch(name) && Directory.Exists(Path.Combine(dir, _options.ProjectDirectoryName)))
                yield return dir;
        }
    }

    private static IEnumerable<string> EnumerateSectionFolders(string path)
    {
        foreach (var dir in Directory.GetDirectories(path))
        {
            if (ContainsSectionAcronym(Path.GetFileName(dir)!))
                yield return dir;
        }
    }

    private static readonly HashSet<string> SectionAcronyms = new(StringComparer.OrdinalIgnoreCase)
        { "AR", "AS", "APT", "KJ", "KR", "KG", "OV", "VK", "EOM", "EM", "PS", "SS", "OViK" };

    private static readonly char[] NameSeparators = ['_', '-', ' ', '.'];

    private static bool ContainsSectionAcronym(string folderName)
    {
        foreach (var part in folderName.Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries))
            if (SectionAcronyms.Contains(part))
                return true;
        return false;
    }

    private static string NewToken() => Guid.NewGuid().ToString("N")[..8];
}
