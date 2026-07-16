namespace TelegramBot.Data;

internal static partial class SqlQueries
{
    internal static class TrackedMessages
    {
        internal const string Insert = @"
            INSERT INTO TrackedMessages (SessionId, ChatId, MessageIdPg)
            VALUES (@SessionId, @ChatId, @MessageIdPg)";

        internal const string DeleteBySession = @"
            DELETE FROM TrackedMessages
            WHERE SessionId = @SessionId";

        internal const string DeleteByChatAndMessages = @"
            DELETE FROM TrackedMessages
            WHERE ChatId = @ChatId AND MessageIdPg = ANY(@MessageIds)";

        internal const string GetByChat = @"
            SELECT MessageIdPg FROM TrackedMessages
            WHERE ChatId = @ChatId
            ORDER BY CreatedAt ASC";
    }
}
