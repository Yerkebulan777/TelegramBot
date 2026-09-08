namespace TelegramBot.Data;

internal static partial class SqlQueries
{
    internal static class TrackedMessages
    {
        internal const string Insert = @"
            INSERT INTO TrackedMessages (SessionId, ChatId, MessageIdPg)
            VALUES (@SessionId, @ChatId, @MessageIdPg)";

        internal const string DeleteByChatAndMessages = @"
            DELETE FROM TrackedMessages
            WHERE ChatId = @ChatId AND MessageIdPg = ANY(@MessageIds)";

        internal const string GetByChat = @"
            SELECT MessageIdPg FROM TrackedMessages
            WHERE ChatId = @ChatId
            ORDER BY CreatedAt ASC";

        internal const string GetForCleanup = @"
            SELECT ChatId, MessageIdPg AS MessageId
            FROM TrackedMessages
            WHERE CreatedAt <= @OlderThan
              AND CreatedAt > @NewerThan
            ORDER BY CreatedAt ASC
            LIMIT @Limit";

        internal const string DeleteOlderThan = @"
            DELETE FROM TrackedMessages
            WHERE CreatedAt <= @OlderThan";
    }
}
