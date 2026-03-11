using System.Text.RegularExpressions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services
{
    public class NavigationService:INavigationService
    {
        //private readonly Dictionary<string, string> _pathMap = new();

        private readonly ISessionManager _sessions;
        static readonly Regex folderRegex = new Regex(


    @"^(\d{2}|\d{3}|I{1,3})_",
    RegexOptions.IgnoreCase
);

        public NavigationService(ISessionManager sessions)
        {
            _sessions = sessions;
        }


        public Task<(string message, InlineKeyboardMarkup keyboard)> GetFilesViewAsync(long userId, string path)
        {
            var session = _sessions.GetOrCreateSession(userId);

            if(string.IsNullOrEmpty(path))
                path = Directory.GetCurrentDirectory();
            //var dirs = Directory.GetDirectories(path);
            var dirs = Directory.GetDirectories(path)
        .Where(d => folderRegex.IsMatch(Path.GetFileName(d)))
        .ToArray();
            var files = Directory.GetFiles(path);
            //Dictionary<string, string> combined = new Dictionary<string, string>();

            var buttons = new List<List<InlineKeyboardButton>>();



            int navElements = 20;

            session.Items.Clear();

            foreach (var dir in dirs)
                {


                    //buttons.Add(new List<InlineKeyboardButton>
                    //{
                    //    InlineKeyboardButton.WithCallbackData($"{Path.GetFileName(dir)}", $"NAV:{token}")
                    //});

                    session.Items.Add(new FileSystemItem { FullPath = dir, Type = ItemType.Directory });
                    //combined.Add(dir, "NAV");


                }
            
            



            foreach (var file in files)
            {
                if (!file.Contains(".rvt"))
                    continue;



                //buttons.Add(new List<InlineKeyboardButton>
                //{
                //    InlineKeyboardButton.WithCallbackData(Path.GetFileName(file),$"FILE:{token}")
                //});

                session.Items.Add(new FileSystemItem { FullPath = file, Type = ItemType.File });
                
                //combined.Add(file, "FILE");

            }

            //session.Items.Sort();


            if (session.Counter < 0)
            {
                session.Counter = 0;
            }
            if (session.Counter > dirs.Length)
            {
                session.Counter -= navElements;
            }


            if (session.Items.Count - session.Counter < navElements)
            {
                for (int i = session.Counter; i < session.Items.Count; i++)
                {

                    //if NAV
                    if (session.Items[i].Type == ItemType.Directory)
                    {
                        string token = Guid.NewGuid().ToString("N").Substring(0, 8);
                        session.PathMap[token] = session.Items[i].FullPath;
                        buttons.Add(new List<InlineKeyboardButton>
                {
                    InlineKeyboardButton.WithCallbackData($"??{Path.GetFileName(session.Items[i].FullPath)}", $"NAV1:{token}")
                });
                    }
                    else if (session.Items[i].Type == ItemType.File)
                    {
                        string token = Guid.NewGuid().ToString("N").Substring(0, 8);
                        session.PathMap[token] = session.Items[i].FullPath;

                        buttons.Add(new List<InlineKeyboardButton>
                {
                    InlineKeyboardButton.WithCallbackData($"??{Path.GetFileName(session.Items[i].FullPath)}", $"FILE:{token}")
                });
                    }

                   //if FILE

                }
            }
            else
            {
                for (int i = session.Counter; i < session.Counter + navElements; i++)
                {

                    if (session.Items[i].Type == ItemType.Directory)
                    {
                        string token = Guid.NewGuid().ToString("N").Substring(0, 8);
                        session.PathMap[token] = session.Items[i].FullPath;
                        buttons.Add(new List<InlineKeyboardButton>
                {
                    InlineKeyboardButton.WithCallbackData($"??{Path.GetFileName(session.Items[i].FullPath)}", $"NAV1:{token}")
                });
                    }
                    else if (session.Items[i].Type == ItemType.File)
                    {
                        string token = Guid.NewGuid().ToString("N").Substring(0, 8);
                        session.PathMap[token] = session.Items[i].FullPath;

                        buttons.Add(new List<InlineKeyboardButton>
                {
                    InlineKeyboardButton.WithCallbackData($"??{Path.GetFileName(session.Items[i].FullPath)}", $"FILE:{token}")
                });
                    }


                }
            }



            buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("??", "PREV:"),
                            InlineKeyboardButton.WithCallbackData("??", "NEXT:")
                        });



            buttons.Add(new List<InlineKeyboardButton>
                       {
                            InlineKeyboardButton.WithCallbackData("?? Выбор: Файлы", "SELMODE:")
                        });



            var parent = Directory.GetParent(path);
            if (parent != null)// and current directory != root directory
            {
                string parentToken = Guid.NewGuid().ToString("N").Substring(0, 8);
                session.PathMap[parentToken] = parent.FullName;


                buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("? Назад",$"NAV2:{parentToken}")
                        });
            }
            buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("?? Продолжить", "APPLYFILES:")
                        });

            buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("? Отменить выбор", "CANCELSEL:")
                        });

            buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("?? Отмена", "CANCELFILESEL:")
                        });



            var markup = new InlineKeyboardMarkup(buttons);
            var message = $"*Current directory:* `{path}`";

            return Task.FromResult((message, markup));
        }
        public bool IsFile(string path) => File.Exists(path);


        public bool TryResolvePath(long userId, string token, out string? path)
        {
            var session = _sessions.GetOrCreateSession(userId);
            
            return session.PathMap.TryGetValue(token, out path);
        }



        public Task<(string message, InlineKeyboardMarkup keyboard)> GetSectionsViewAsync(long userId, string path)
        {
            var session = _sessions.GetOrCreateSession(userId);

            //if (string.IsNullOrEmpty(path))
            //    path = Directory.GetCurrentDirectory();
            //var dirs = Directory.GetDirectories(path);
            var dirs = Directory.GetDirectories(path)
        .Where(d => folderRegex.IsMatch(Path.GetFileName(d)))
        .ToArray();
            //var files = Directory.GetFiles(path);

            var buttons = new List<List<InlineKeyboardButton>>();
            int navElements = 20;



            if (session.Level == false)
            {
                if (session.Counter < 0)
                {
                    session.Counter = 0;
                }
                if (session.Counter > dirs.Length)
                {
                    session.Counter -= navElements;
                }


                if (dirs.Length - session.Counter < navElements)
                {
                    for (int i = session.Counter; i < dirs.Length - 1; i++)
                    {
                        string token = Guid.NewGuid().ToString("N").Substring(0, 8);
                        session.PathMap[token] = dirs[i];

                        buttons.Add(new List<InlineKeyboardButton>
                {
                    InlineKeyboardButton.WithCallbackData($"??{Path.GetFileName(dirs[i])}", $"NAV1:{token}")
                });
                    }
                }
                else
                {
                    for (int i = session.Counter; i < session.Counter + navElements; i++)
                    {
                        string token = Guid.NewGuid().ToString("N").Substring(0, 8);
                        session.PathMap[token] = dirs[i];

                        buttons.Add(new List<InlineKeyboardButton>
                {
                    InlineKeyboardButton.WithCallbackData($"??{Path.GetFileName(dirs[i])}", $"NAV1:{token}")
                });
                    }
                }
            }
            
            if(session.Level==true)
            {
                if (session.Counter < 0)
                {
                    session.Counter = 0;
                }
                if (session.Counter > dirs.Length)
                {
                    session.Counter -= navElements;
                }


                if (dirs.Length - session.Counter < navElements)
                {
                    for (int i = session.Counter; i < dirs.Length - 1; i++)
                    {
                        string token = Guid.NewGuid().ToString("N").Substring(0, 8);
                        session.PathMap[token] = dirs[i];

                        buttons.Add(new List<InlineKeyboardButton>
                {
                    InlineKeyboardButton.WithCallbackData($"??{Path.GetFileName(dirs[i])}", $"FILE:{token}")
                });
                    }
                }
                else
                {
                    for (int i = session.Counter; i < session.Counter + navElements; i++)
                    {
                        string token = Guid.NewGuid().ToString("N").Substring(0, 8);
                        session.PathMap[token] = dirs[i];

                        buttons.Add(new List<InlineKeyboardButton>
                {
                    InlineKeyboardButton.WithCallbackData($"??{Path.GetFileName(dirs[i])}", $"FILE:{token}")
                });
                    }
                }
            }



            buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("??", "PREV:"),
                            InlineKeyboardButton.WithCallbackData("??", "NEXT:")
                        });







            buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("?? Выбор: Разделы", "SELMODE:")
                        });


            var parent = Directory.GetParent(path);
            if (parent != null)// and current directory != root directory
            {
                string parentToken = Guid.NewGuid().ToString("N").Substring(0, 8);
                session.PathMap[parentToken] = parent.FullName;


                buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("? Назад",$"NAV2:{parentToken}")
                        });
            }
            
            buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("?? Продолжить", "APPLYFILES:")
                        });

            buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("? Отменить выбор", "CANCELSEL:")
                        });

            buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("?? Отмена", "CANCELFILESEL:")
                        });



            var markup = new InlineKeyboardMarkup(buttons);
            var message = $"*Current directory:* `{path}`";
            

            return Task.FromResult((message, markup));
        }
        public Task<(string message, InlineKeyboardMarkup keyboard)> GetProjectsViewAsync(long userId, string path)
        {
            var session = _sessions.GetOrCreateSession(userId);

            //if (string.IsNullOrEmpty(path))
            //    path = Directory.GetCurrentDirectory();
            var dirs = Directory.GetDirectories(path)
        .Where(d => folderRegex.IsMatch(Path.GetFileName(d)))
        .ToArray();
            //var dirs = Directory.GetDirectories(path);

            var buttons = new List<List<InlineKeyboardButton>>();
            int navElements = 20;



            if (session.Counter<0)
            {
                session.Counter = 0;
            }
            if (session.Counter > dirs.Length)
            {
                session.Counter-= navElements;
            }


            if (dirs.Length - session.Counter < navElements)
            {
                for (int i = session.Counter; i < dirs.Length - 1; i++)
                {
                    string token = Guid.NewGuid().ToString("N").Substring(0, 8);
                    session.PathMap[token] = dirs[i];

                    buttons.Add(new List<InlineKeyboardButton>
                {
                    InlineKeyboardButton.WithCallbackData($"??{Path.GetFileName(dirs[i])}", $"FILE:{token}")
                });
                }
            }
            else
            {
                for (int i = session.Counter; i < session.Counter + navElements; i++)
                {
                    string token = Guid.NewGuid().ToString("N").Substring(0, 8);
                    session.PathMap[token] = dirs[i];

                    buttons.Add(new List<InlineKeyboardButton>
                {
                    InlineKeyboardButton.WithCallbackData($"??{Path.GetFileName(dirs[i])}", $"FILE:{token}")
                });
                }
            }





              



            buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("??", "PREV:"),
                            InlineKeyboardButton.WithCallbackData("??", "NEXT:")
                        });




            buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("?? Выбор: Проекты", "SELMODE:")
                        });

            var parent = Directory.GetParent(path);
            if (parent != null)// and current directory != root directory
            {
                string parentToken = Guid.NewGuid().ToString("N").Substring(0, 8);
                session.PathMap[parentToken] = parent.FullName;


                buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("? Назад",$"NAV2:{parentToken}")
                        });
            }

            buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("?? Продолжить", "APPLYFILES:")
                        });

            buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("? Отменить выбор", "CANCELSEL:")
                        });

            buttons.Add(new List<InlineKeyboardButton>
                        {
                            InlineKeyboardButton.WithCallbackData("?? Отмена", "CANCELFILESEL:")
                        });



            var markup = new InlineKeyboardMarkup(buttons);
            var message = $"*Current directory:* `{path}`";

            return Task.FromResult((message, markup));
        }

    }
}
