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
        var session = _sessions.GetOrCreateSession(userId);
        return session.PathMap.TryGetValue(token, out path);
    }

    public Task<(string message, InlineKeyboardMarkup keyboard)> GetSectionsViewAsync(long userId, string path)
    {
        var session = _sessions.GetOrCreateSession(userId);

        var dirs = Directory.GetDirectories(path)
            .Where(d => _folderRegex.IsMatch(Path.GetFileName(d)));

        if (IsRootPath(path))
        {
            dirs = dirs.Where(d => Directory.Exists(Path.Combine(d, _options.ProjectDirectoryName)));
        }

        session.PathMap.Clear();

        var buttons = new List<List<InlineKeyboardButton>>();
        var items = dirs.Select(d => new FileSystemItem { FullPath = d, Type = ItemType.Directory }).ToList();

        session.SetItems(items);

        var selectedFiles = session.SelectedFiles;

        foreach (var item in items)
        {
            var prefix = selectedFiles.Contains(item.FullPath) ? "✅ " : "📁 ";
            string token = Guid.NewGuid().ToString("N")[..8];
            session.PathMap[token] = item.FullPath;
            buttons.Add([InlineKeyboardButton.WithCallbackData($"{prefix}{Path.GetFileName(item.FullPath)}", $"{CallbackPrefixes.OpenFolder}{token}")]);
        }

        var parent = Directory.GetParent(path);
        if (parent != null && IsPathWithinRoot(parent.FullName))
        {
            string parentToken = Guid.NewGuid().ToString("N")[..8];
            session.PathMap[parentToken] = parent.FullName;
            buttons.Add([InlineKeyboardButton.WithCallbackData("⬅️ Назад", $"{CallbackPrefixes.GoToParent}{parentToken}")]);
        }

        return Task.FromResult(($"*Current directory:* `{path}`", new InlineKeyboardMarkup(buttons)));
    }

    private bool IsPathWithinRoot(string path)
    {
        try
        {
            var rootFullPath = Path.GetFullPath(_options.RootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidateFullPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return candidateFullPath.Equals(rootFullPath, StringComparison.OrdinalIgnoreCase)
                   || candidateFullPath.StartsWith(
                       rootFullPath + Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool IsRootPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return true;
        try
        {
            var rootFullPath = Path.GetFullPath(_options.RootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var pathFullPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return pathFullPath.Equals(rootFullPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
