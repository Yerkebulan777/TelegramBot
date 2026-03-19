using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
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

    public async Task<(string message, InlineKeyboardMarkup keyboard)> GetFilesViewAsync(long userId, string path)
    {
        var session = _sessions.GetOrCreateSession(userId);

        if (string.IsNullOrEmpty(path))
            path = Directory.GetCurrentDirectory();

        var (dirs, files) = await Task.Run(() =>
        {
            var d = Directory.GetDirectories(path)
                .Where(x => _folderRegex.IsMatch(Path.GetFileName(x)))
                .ToArray();
            var f = Directory.GetFiles(path);
            return (d, f);
        });

        var buttons = new List<List<InlineKeyboardButton>>();

        session.PathMap.Clear();

        var items = new List<FileSystemItem>();
        foreach (var dir in dirs)
            items.Add(new FileSystemItem { FullPath = dir, Type = ItemType.Directory });

        foreach (var file in files)
        {
            if (!_options.IsRevitFile(file))
                continue;
            items.Add(new FileSystemItem { FullPath = file, Type = ItemType.File });
        }

        session.SetItems(items);

        var selectedFiles = session.SelectedFiles;

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var isSelected = item.Type == ItemType.File && selectedFiles.Contains(item.FullPath);
            var prefix = isSelected ? "✅ " : (item.Type == ItemType.Directory ? "📁 " : "📄 ");
            var callbackPrefix = item.Type == ItemType.Directory ? "OPENFOLDER:" : "FILE:";

            string token = Guid.NewGuid().ToString("N")[..8];
            session.PathMap[token] = item.FullPath;
            buttons.Add([
                InlineKeyboardButton.WithCallbackData($"{prefix}{Path.GetFileName(item.FullPath)}", $"{callbackPrefix}{token}")
            ]);
        }

        AddNavigationButtons(buttons, session, path);

        var markup = new InlineKeyboardMarkup(buttons);
        var message = $"*Current directory:* `{path}`";
        return (message, markup);
    }

    public bool TryResolvePath(long userId, string token, out string? path)
    {
        var session = _sessions.GetOrCreateSession(userId);
        return session.PathMap.TryGetValue(token, out path);
    }

    public Task<(string message, InlineKeyboardMarkup keyboard)> GetSectionsViewAsync(long userId, string path)
        => GetDirectoriesViewAsync(userId, path, "OPENFOLDER:");

    public Task<(string message, InlineKeyboardMarkup keyboard)> GetProjectsViewAsync(long userId, string path)
        => GetDirectoriesViewAsync(userId, path, "FILE:");

    private void AddNavigationButtons(List<List<InlineKeyboardButton>> buttons, UserSession session, string path)
    {
        var selectionLabel = session.SelectionType switch
        {
            SelectionMode.Sections => "🌟 Режим: [ РАЗДЕЛЫ ]",
            SelectionMode.Projects => "🌟 Режим: [ ПРОЕКТЫ ]",
            _ => "🌟 Режим: [ ФАЙЛЫ ]"
        };

        buttons.Add([InlineKeyboardButton.WithCallbackData(selectionLabel, "SELMODE:")]);

        var parent = Directory.GetParent(path);
        if (parent != null && IsPathWithinRoot(parent.FullName))
        {
            string parentToken = Guid.NewGuid().ToString("N")[..8];
            session.PathMap[parentToken] = parent.FullName;
            buttons.Add([InlineKeyboardButton.WithCallbackData("⬅️ Назад", $"GOTOPARENT:{parentToken}")]);
        }
    }

    private async Task<(string message, InlineKeyboardMarkup keyboard)> GetDirectoriesViewAsync(
        long userId, string path, string itemCallbackPrefix)
    {
        var session = _sessions.GetOrCreateSession(userId);

        var dirs = await Task.Run(() =>
            Directory.GetDirectories(path)
                .Where(d => _folderRegex.IsMatch(Path.GetFileName(d)))
                .ToArray());

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
            buttons.Add([InlineKeyboardButton.WithCallbackData($"{prefix}{Path.GetFileName(item.FullPath)}", $"{itemCallbackPrefix}{token}")]);
        }

        AddNavigationButtons(buttons, session, path);

        return ($"*Current directory:* `{path}`", new InlineKeyboardMarkup(buttons));
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
}
