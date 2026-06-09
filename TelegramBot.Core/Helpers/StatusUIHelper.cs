namespace TelegramBot.Core.Helpers;

public static class StatusUIHelper
{
    public static string GetCommandStatusIcon(string status)
    {
        return status switch
        {
            "pending" => "⏳",
            "processing" => "🔄",
            "Done" => "✅",
            "Failed" => "❌",
            "Deleted" => "🗑",
            _ => "❓"
        };
    }

    public static string GetSessionStatusIcon(string status, int activeCommands)
    {
        return activeCommands > 0
            ? "🔄"
            : status switch
            {
                "Done" => "✅",
                "Failed" => "❌",
                "Deleted" => "🗑",
                _ => "📋"
            };
    }
}
