using System.Text.RegularExpressions;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services
{
    public partial class FileSystemBrowser : IFileSystemBrowser
    {
        //private readonly Dictionary<string, string> _pathMap = new();

        private readonly ISessionManager _sessions;
        static readonly Regex folderRegex = MyRegex();

        public FileSystemBrowser(ISessionManager sessions)
        {
            _sessions = sessions;
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
                    .Where(x => folderRegex.IsMatch(Path.GetFileName(x)))
                    .ToArray();
                var f = Directory.GetFiles(path);
                return (d, f);
            });

            var buttons = new List<List<InlineKeyboardButton>>();

            // Clear stale token→path mappings to prevent PathMap growing unbounded
            session.PathMap.Clear();
            session.Items.Clear();

            foreach (var dir in dirs)
                session.Items.Add(new FileSystemItem { FullPath = dir, Type = ItemType.Directory });

            foreach (var file in files)
            {
                if (!file.Contains(".rvt"))
                    continue;
                session.Items.Add(new FileSystemItem { FullPath = file, Type = ItemType.File });
            }

            for (int i = 0; i < session.Items.Count; i++)
            {
                if (session.Items[i].Type == ItemType.Directory)
                {
                    string token = Guid.NewGuid().ToString("N").Substring(0, 8);
                    session.PathMap[token] = session.Items[i].FullPath;
                    buttons.Add(new List<InlineKeyboardButton>
                    {
                        InlineKeyboardButton.WithCallbackData($"📁 {Path.GetFileName(session.Items[i].FullPath)}", $"OPENFOLDER:{token}")
                    });
                }
                else if (session.Items[i].Type == ItemType.File)
                {
                    string token = Guid.NewGuid().ToString("N").Substring(0, 8);
                    session.PathMap[token] = session.Items[i].FullPath;
                    buttons.Add(new List<InlineKeyboardButton>
                    {
                        InlineKeyboardButton.WithCallbackData($"📄 {Path.GetFileName(session.Items[i].FullPath)}", $"FILE:{token}")
                    });
                }
            }

            buttons.Add(new List<InlineKeyboardButton>
            {
                InlineKeyboardButton.WithCallbackData("🔄 Выбор: Файлы", "SELMODE:")
            });

            var parent = Directory.GetParent(path);
            if (parent != null)
            {
                string parentToken = Guid.NewGuid().ToString("N").Substring(0, 8);
                session.PathMap[parentToken] = parent.FullName;
                buttons.Add(new List<InlineKeyboardButton>
                {
                    InlineKeyboardButton.WithCallbackData("⬅️ Назад", $"GOTOPARENT:{parentToken}")
                });
            }

            buttons.Add(new List<InlineKeyboardButton>
            {
                InlineKeyboardButton.WithCallbackData("✅ Продолжить", "APPLYFILES:")
            });
            buttons.Add(new List<InlineKeyboardButton>
            {
                InlineKeyboardButton.WithCallbackData("🔄 Отменить выбор", "CANCELSEL:")
            });
            buttons.Add(new List<InlineKeyboardButton>
            {
                InlineKeyboardButton.WithCallbackData("❌ Отмена", "CANCELFILESEL:")
            });

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



        public async Task<(string message, InlineKeyboardMarkup keyboard)> GetSectionsViewAsync(long userId, string path)
        {
            var session = _sessions.GetOrCreateSession(userId);

            // Offload blocking network/filesystem I/O off the ThreadPool handler thread
            var dirs = await Task.Run(() =>
                Directory.GetDirectories(path)
                    .Where(d => folderRegex.IsMatch(Path.GetFileName(d)))
                    .ToArray());

            // Clear stale token→path mappings to prevent PathMap growing unbounded
            session.PathMap.Clear();

            var buttons = new List<List<InlineKeyboardButton>>();
            if (session.Level == false)
            {
                for (int i = 0; i < dirs.Length; i++)
                {
                    string token = Guid.NewGuid().ToString("N").Substring(0, 8);
                    session.PathMap[token] = dirs[i];
                    buttons.Add(new List<InlineKeyboardButton>
                    {
                        InlineKeyboardButton.WithCallbackData($"📁 {Path.GetFileName(dirs[i])}", $"OPENFOLDER:{token}")
                    });
                }
            }

            if (session.Level == true)
            {
                for (int i = 0; i < dirs.Length; i++)
                {
                    string token = Guid.NewGuid().ToString("N").Substring(0, 8);
                    session.PathMap[token] = dirs[i];
                    buttons.Add(new List<InlineKeyboardButton>
                    {
InlineKeyboardButton.WithCallbackData($"📁 {Path.GetFileName(dirs[i])}", $"OPENFOLDER:{token}")
                    });
                }
            }

            buttons.Add(new List<InlineKeyboardButton>
            {
                InlineKeyboardButton.WithCallbackData("🔄 Выбор: Разделы", "SELMODE:")
            });

            var parent = Directory.GetParent(path);
            if (parent != null)
            {
                string parentToken = Guid.NewGuid().ToString("N").Substring(0, 8);
                session.PathMap[parentToken] = parent.FullName;
                buttons.Add(new List<InlineKeyboardButton>
                {
                    InlineKeyboardButton.WithCallbackData("⬅️ Назад", $"GOTOPARENT:{parentToken}")
                });
            }

            buttons.Add(new List<InlineKeyboardButton>
            {
                InlineKeyboardButton.WithCallbackData("✅ Продолжить", "APPLYFILES:")
            });
            buttons.Add(new List<InlineKeyboardButton>
            {
                InlineKeyboardButton.WithCallbackData("🔄 Отменить выбор", "CANCELSEL:")
            });
            buttons.Add(new List<InlineKeyboardButton>
            {
                InlineKeyboardButton.WithCallbackData("❌ Отмена", "CANCELFILESEL:")
            });

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
                    .Where(d => folderRegex.IsMatch(Path.GetFileName(d)))
                    .ToArray());

            // Clear stale token→path mappings to prevent PathMap growing unbounded
            session.PathMap.Clear();

            var buttons = new List<List<InlineKeyboardButton>>();
            for (int i = 0; i < dirs.Length; i++)
            {
                string token = Guid.NewGuid().ToString("N").Substring(0, 8);
                session.PathMap[token] = dirs[i];
                buttons.Add(new List<InlineKeyboardButton>
                {
                    InlineKeyboardButton.WithCallbackData($"📁 {Path.GetFileName(dirs[i])}", $"FILE:{token}")
                });
            }

            buttons.Add(new List<InlineKeyboardButton>
            {
                InlineKeyboardButton.WithCallbackData("🔄 Выбор: Проекты", "SELMODE:")
            });

            var parent = Directory.GetParent(path);
            if (parent != null)
            {
                string parentToken = Guid.NewGuid().ToString("N").Substring(0, 8);
                session.PathMap[parentToken] = parent.FullName;
                buttons.Add(new List<InlineKeyboardButton>
                {
                    InlineKeyboardButton.WithCallbackData("⬅️ Назад", $"GOTOPARENT:{parentToken}")
                });
            }

            buttons.Add(new List<InlineKeyboardButton>
            {
                InlineKeyboardButton.WithCallbackData("✅ Продолжить", "APPLYFILES:")
            });
            buttons.Add(new List<InlineKeyboardButton>
            {
                InlineKeyboardButton.WithCallbackData("🔄 Отменить выбор", "CANCELSEL:")
            });
            buttons.Add(new List<InlineKeyboardButton>
            {
                InlineKeyboardButton.WithCallbackData("❌ Отмена", "CANCELFILESEL:")
            });

            var markup = new InlineKeyboardMarkup(buttons);
            var message = $"*Current directory:* `{path}`";
            return (message, markup);
        }

        [GeneratedRegex(

            @"^(\d{2}|\d{3}|I{1,3})_", RegexOptions.IgnoreCase, "ru-KZ")]
        private static partial Regex MyRegex();
    }
}
