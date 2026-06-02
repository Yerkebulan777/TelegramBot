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
            var prefix = session.IsFileSelectionActive ? CallbackPrefixes.File : CallbackPrefixes.OpenFolder;
            buttons.Add([InlineKeyboardButton.WithCallbackData(label, $"{prefix}{token}")]);
        }

        var parent = Directory.GetParent(path);
        if (parent is not null && _options.IsPathWithinRoot(parent.FullName))
        {
            string parentToken = NewToken();
            session.PathMap[parentToken] = parent.FullName;
            buttons.Add([InlineKeyboardButton.WithCallbackData("⬅️ Назад", $"{CallbackPrefixes.GoToParent}{parentToken}")]);
        }

        return Task.FromResult(new InlineKeyboardMarkup(buttons));
    }

    private IEnumerable<string> EnumerateSectionDirectories(string path)
    {
        foreach (var dir in Directory.GetDirectories(path))
        {
            var name = Path.GetFileName(dir);
            if (!_folderRegex.IsMatch(name))
                continue;
            if (!Directory.Exists(Path.Combine(dir, _options.ProjectDirectoryName)))
                continue;
            yield return dir;
        }
    }

    private static string Prefix(bool isSelected) => isSelected ? "✅ " : "📁 ";

    private static string NewToken() => Guid.NewGuid().ToString("N")[..8];
}
