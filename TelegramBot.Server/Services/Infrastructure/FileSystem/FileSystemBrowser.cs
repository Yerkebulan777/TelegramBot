using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Config;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Infrastructure.FileSystem;

/// <summary>
/// Browses the filesystem and builds inline keyboard representations.
/// </summary>
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
        var selected = session.SelectedFiles;
        var buttons = new List<List<InlineKeyboardButton>>();

        session.PathMap.Clear();

        foreach (var dir in EnumerateSectionDirectories(path))
        {
            string token = NewToken();
            session.PathMap[token] = dir;
            string label = $"{Prefix(selected.Contains(dir))}{Path.GetFileName(dir)}";
            buttons.Add([InlineKeyboardButton.WithCallbackData(label, $"{CallbackPrefixes.File}{token}")]);
        }

        var parent = Directory.GetParent(path);
        bool hasParent = parent is not null && _options.IsPathWithinRoot(parent.FullName);

        var actionRow = new List<InlineKeyboardButton>
        {
            InlineKeyboardButton.WithCallbackData("✅ Подтвердить", CallbackPrefixes.ApplyFiles)
        };

        if (hasParent)
        {
            string parentToken = NewToken();
            session.PathMap[parentToken] = parent!.FullName;
            actionRow.Add(InlineKeyboardButton.WithCallbackData("⬅️ Назад", $"{CallbackPrefixes.GoToParent}{parentToken}"));
        }

        actionRow.Add(InlineKeyboardButton.WithCallbackData("❌ Отмена", CallbackPrefixes.CancelFileSelection));
        buttons.Add(actionRow);

        return Task.FromResult(new InlineKeyboardMarkup(buttons));
    }

    private static readonly HashSet<string> SectionAcronyms = new(StringComparer.OrdinalIgnoreCase)
        { "AR", "AS", "APT", "KJ", "KR", "KG", "OV", "VK", "EOM", "EM", "PS", "SS", "OViK" };

    private static readonly char[] NameSeparators = ['_', '-', ' ', '.'];

    private IEnumerable<string> EnumerateSectionDirectories(string path)
    {
        bool insideProjectDir = string.Equals(
            Path.GetFileName(path), _options.ProjectDirectoryName,
            StringComparison.OrdinalIgnoreCase);

        foreach (var dir in Directory.GetDirectories(path))
        {
            var name = Path.GetFileName(dir)!;

            if (insideProjectDir)
            {
                if (ContainsSectionAcronym(name))
                    yield return dir;
            }
            else
            {
                if (!_folderRegex.IsMatch(name)) continue;
                if (!Directory.Exists(Path.Combine(dir, _options.ProjectDirectoryName))) continue;
                yield return dir;
            }
        }
    }

    private static bool ContainsSectionAcronym(string folderName)
    {
        foreach (var part in folderName.Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries))
            if (SectionAcronyms.Contains(part))
                return true;
        return false;
    }

    private static string Prefix(bool isSelected) => isSelected ? "✅ " : "📁 ";

    private static string NewToken() => Guid.NewGuid().ToString("N")[..8];
}
