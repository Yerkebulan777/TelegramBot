namespace TelegramBot.Data;

internal static partial class SqlQueries
{
    internal static class TrackedMessages
    {
        internal const string Insert =
            "INSERT INTO TrackedMessages (UserId, MessageId) VALUES (@UserId, @MessageId) ON CONFLICT DO NOTHING;";

        internal const string InsertBatch =
            "INSERT INTO TrackedMessages (UserId, MessageId) SELECT @UserId, unnest(@MessageIds::int[]) ON CONFLICT DO NOTHING;";

        internal const string GetAll =
            "SELECT UserId, MessageId FROM TrackedMessages;";

        internal const string GetByUser =
            "SELECT MessageId FROM TrackedMessages WHERE UserId = @UserId;";

        internal const string DeleteByUser =
            "DELETE FROM TrackedMessages WHERE UserId = @UserId;";

        internal const string DeleteSingle =
            "DELETE FROM TrackedMessages WHERE UserId = @UserId AND MessageId = @MessageId;";
    }
}
