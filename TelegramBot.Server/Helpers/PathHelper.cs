namespace TelegramBot.Server.Helpers;

public static class PathHelper
{
    public static string GetSafePathName(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }
}
