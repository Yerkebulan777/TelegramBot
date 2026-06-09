using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot.Core.Config;
using TelegramBot.Core.Constants;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;

namespace TelegramBot.Server.Services.Infrastructure.FileSystem;

public class FileSystemBrowser(ISessionManager sessions, IOptions<FileSystemOptions> options)
{
    private readonly FileSystemOptions _options = options.Value;
    private readonly Regex _folderRegex = new(options.Value.SectionFolderPattern, RegexOptions.IgnoreCase);

    private static readonly HashSet<string> _sectionAcronyms = new(StringComparer.OrdinalIgnoreCase)
    {
        "AR", "AS", "APT", "KJ", "KR", "KG", "OV", "VK", "EOM", "EM", "PS", "SS", "OViK"
    };

    private static readonly char[] _nameSeparators = ['_', '-', ' ', '.'];

    public Task<InlineKeyboardMarkup> GetSectionsViewAsync(long userId, string path)
    {
        var session = sessions.GetOrCreateSession(userId);

        var atSectionLevel = string.Equals(
            Path.GetFileName(path), _options.ProjectDirectoryName,
            StringComparison.OrdinalIgnoreCase);

        var keyboard = atSectionLevel
            ? BuildSectionKeyboard(session, path)
            : BuildProjectKeyboard(session, path);

        return Task.FromResult(keyboard);
    }

    private InlineKeyboardMarkup BuildProjectKeyboard(UserSession session, string path)
    {
        var selected = session.GetSelectedFiles();
        var buttons = new List<List<InlineKeyboardButton>>();

        foreach (var dir in EnumerateProjectFolders(path))
        {
            var label = $"{(selected.Contains(dir) ? "✅ " : "📁 ")}{Path.GetFileName(dir)}";
            buttons.Add([InlineKeyboardButton.WithCallbackData(label, $"{CallbackPrefixes.File}{dir}")]);
        }

        return new InlineKeyboardMarkup(buttons);
    }

    private static InlineKeyboardMarkup BuildSectionKeyboard(UserSession session, string path)
    {
        var selected = session.GetSelectedFiles();
        var buttons = new List<List<InlineKeyboardButton>>();

        foreach (var dir in EnumerateSectionFolders(path))
        {
            var label = $"{(selected.Contains(dir) ? "✅ " : "📁 ")}{Path.GetFileName(dir)}";
            buttons.Add([InlineKeyboardButton.WithCallbackData(label, $"{CallbackPrefixes.File}{dir}")]);
        }

        return new InlineKeyboardMarkup(buttons);
    }

    private IEnumerable<string> EnumerateProjectFolders(string path)
    {
        foreach (var dir in Directory.GetDirectories(path))
        {
            var name = Path.GetFileName(dir)!;

            if (_folderRegex.IsMatch(name) && Directory.Exists(Path.Combine(dir, _options.ProjectDirectoryName)))
            {
                yield return dir;
            }
        }
    }

    private static IEnumerable<string> EnumerateSectionFolders(string path)
    {
        return Directory.GetDirectories(path).Where(dir => ContainsSectionAcronym(Path.GetFileName(dir)));
    }

    private static bool ContainsSectionAcronym(string folderName)
    {
        var nameSegments = folderName.Split(_nameSeparators, StringSplitOptions.RemoveEmptyEntries);
        return nameSegments.Any(_sectionAcronyms.Contains);
    }

}
