using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotServer.Config;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services
{
    public partial class FileSystemBrowser : IFileSystemBrowser
    {
        private readonly ISessionManager _sessions;
        private readonly FileSystemOptions _options;
        private readonly Regex _folderRegex;
        private readonly Regex _romanThreeRegex;

        public FileSystemBrowser(ISessionManager sessions, IOptions<FileSystemOptions> options)
        {
            _sessions = sessions;
            _options = options.Value;
            _folderRegex = new Regex(_options.SectionFolderPattern, RegexOptions.IgnoreCase);
            _romanThreeRegex = new Regex(_options.RomanThreePattern, RegexOptions.IgnoreCase);
        }


        public async Task<(string message, InlineKeyboardMarkup keyboard)> GetFilesViewAsync(long userId, string path)
        {
            var session = _sessions.GetOrCreateSession(userId);

            if (string.IsNullOrEmpty(path))
                path = Directory.GetCurrentDirectory();

            // Offload blocking network/filesystem I/O off the ThreadPool handler thread
            var (dirs, files) = await Task.Run(() =>
            {
                var d = Directory.GetDirectories(path)
                    .Where(x => _folderRegex.IsMatch(Path.GetFileName(x)))
                    .ToArray();
                var f = Directory.GetFiles(path);
                return (d, f);
            });

            var buttons = new List<List<InlineKeyboardButton>>();

            // Clear stale token→path mappings to prevent PathMap growing unbounded
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
        public bool IsFile(string path) => File.Exists(path);


        public bool TryResolvePath(long userId, string token, out string? path)
        {
            var session = _sessions.GetOrCreateSession(userId);

            return session.PathMap.TryGetValue(token, out path);
        }

        private void AddNavigationButtons(List<List<InlineKeyboardButton>> buttons, UserSession session, string path)
        {
            var selectionLabel = session.SelectionType switch
            {
                SelectionMode.Sections => "🔄 Выбор: Разделы",
                SelectionMode.Projects => "🔄 Выбор: Проекты",
                _ => "🔄 Выбор: Файлы"
            };

            buttons.Add([InlineKeyboardButton.WithCallbackData(selectionLabel, "SELMODE:")]);

            var parent = Directory.GetParent(path);
            if (parent != null)
            {
                string parentToken = Guid.NewGuid().ToString("N")[..8];
                session.PathMap[parentToken] = parent.FullName;
                buttons.Add([InlineKeyboardButton.WithCallbackData("⬅️ Назад", $"GOTOPARENT:{parentToken}")]);
            }

            buttons.Add([InlineKeyboardButton.WithCallbackData("✅ Продолжить", "APPLYFILES:")]);
            buttons.Add([InlineKeyboardButton.WithCallbackData("🔄 Отменить выбор", "CANCELSEL:")]);
            buttons.Add([InlineKeyboardButton.WithCallbackData("❌ Отмена", "CANCELFILESEL:")]);
        }



        public async Task<(string message, InlineKeyboardMarkup keyboard)> GetSectionsViewAsync(long userId, string path)
        {
            var session = _sessions.GetOrCreateSession(userId);

            // Offload blocking network/filesystem I/O off the ThreadPool handler thread
            var dirs = await Task.Run(() =>
                Directory.GetDirectories(path)
                    .Where(d => _folderRegex.IsMatch(Path.GetFileName(d)))
                    .ToArray());

            // Clear stale token→path mappings to prevent PathMap growing unbounded
            session.PathMap.Clear();

            var buttons = new List<List<InlineKeyboardButton>>();

            var items = new List<FileSystemItem>();
            foreach (var dir in dirs)
                items.Add(new FileSystemItem { FullPath = dir, Type = ItemType.Directory });

            session.SetItems(items);

            var selectedFiles = session.SelectedFiles;

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var isSelected = selectedFiles.Contains(item.FullPath);
                var prefix = isSelected ? "✅ " : "📁 ";

                string token = Guid.NewGuid().ToString("N")[..8];
                session.PathMap[token] = item.FullPath;
                buttons.Add([InlineKeyboardButton.WithCallbackData($"{prefix}{Path.GetFileName(item.FullPath)}", $"OPENFOLDER:{token}")]);
            }

            AddNavigationButtons(buttons, session, path);

            var markup = new InlineKeyboardMarkup(buttons);
            var message = $"*Current directory:* `{path}`";
            return (message, markup);
        }

        public async Task<(string message, InlineKeyboardMarkup keyboard)> GetProjectsViewAsync(long userId, string path)
        {
            var session = _sessions.GetOrCreateSession(userId);

            // Offload blocking network/filesystem I/O off the ThreadPool handler thread
            var dirs = await Task.Run(() =>
                Directory.GetDirectories(path)
                    .Where(d => _folderRegex.IsMatch(Path.GetFileName(d)))
                    .ToArray());

            // Clear stale token→path mappings to prevent PathMap growing unbounded
            session.PathMap.Clear();

            var buttons = new List<List<InlineKeyboardButton>>();

            var items = new List<FileSystemItem>();
            foreach (var dir in dirs)
                items.Add(new FileSystemItem { FullPath = dir, Type = ItemType.Directory });

            session.SetItems(items);

            var selectedFiles = session.SelectedFiles;

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var isSelected = selectedFiles.Contains(item.FullPath);
                var prefix = isSelected ? "✅ " : "📁 ";

                string token = Guid.NewGuid().ToString("N")[..8];
                session.PathMap[token] = item.FullPath;
                buttons.Add([InlineKeyboardButton.WithCallbackData($"{prefix}{Path.GetFileName(item.FullPath)}", $"FILE:{token}")]);
            }

            AddNavigationButtons(buttons, session, path);

            var markup = new InlineKeyboardMarkup(buttons);
            var message = $"*Current directory:* `{path}`";
            return (message, markup);
        }

        /// <summary>
        /// Gets the regex for matching Roman numeral III sections.
        /// Used by CommandAppService for MapProjectsToFilesAsync.
        /// </summary>
        public Regex GetRomanThreeRegex() => _romanThreeRegex;
    }
}
