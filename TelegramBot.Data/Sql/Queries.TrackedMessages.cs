namespace TelegramBot.Data;

internal static partial class SqlQueries
{
    internal static class TrackedMessages
    {
        internal const string Insert =
            "INSERT OR IGNORE INTO TrackedMessages (UserId, MessageId) VALUES (@UserId, @MessageId);";

        internal const string GetAll =
            "SELECT UserId, MessageId FROM TrackedMessages;";

        internal const string DeleteByUser =
            "DELETE FROM TrackedMessages WHERE UserId = @UserId;";
    }
}
