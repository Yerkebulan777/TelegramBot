namespace TelegramBot.Data;

internal static partial class SqlQueries
{
    internal static class TrackedMessages
    {
        internal const string Insert = @"
            INSERT INTO TrackedMessages (SessionId, ChatId, MessageIdPg, Kind, CreatedAt, DeleteAfter)
            VALUES (@SessionId, @ChatId, @MessageIdPg, @Kind, @SentAt, @DeleteAfter)
            ON CONFLICT (ChatId, MessageIdPg) DO NOTHING";

        internal const string ScheduleDeletionByChat = @"
            WITH scheduled AS (
                INSERT INTO TrackedMessages (ChatId, MessageIdPg, Kind, DeleteAfter)
                SELECT @ChatId, messageId, @InterfaceKind, NOW()
                FROM unnest(@MessageIds) AS messageId
                WHERE TRUE
                ON CONFLICT (ChatId, MessageIdPg) DO UPDATE
                SET DeleteAfter = LEAST(TrackedMessages.DeleteAfter, NOW())
                WHERE TrackedMessages.Kind <> @CompletionKind
                RETURNING MessageIdPg, NextDeleteAttemptAt
            )
            SELECT MessageIdPg FROM scheduled WHERE NextDeleteAttemptAt <= NOW()";

        internal const string ScheduleDeletionBySession = @"
            UPDATE TrackedMessages
            SET DeleteAfter = LEAST(DeleteAfter, NOW()),
                NextDeleteAttemptAt = NOW()
            WHERE SessionId = @SessionId
            RETURNING ChatId, MessageIdPg AS MessageId";

        internal const string ScheduleMessagesOfDeletedSessions = @"
            UPDATE TrackedMessages t
            SET DeleteAfter = LEAST(t.DeleteAfter, NOW()),
                NextDeleteAttemptAt = LEAST(t.NextDeleteAttemptAt, NOW())
            FROM Sessions s
            WHERE t.SessionId = s.SessionId
              AND s.Status = 'Deleted'
              AND (t.DeleteAfter IS NULL OR t.DeleteAfter > NOW())";

        internal const string SaveDeletionProgress = @"
            WITH removed AS (
                DELETE FROM TrackedMessages
                WHERE ChatId = @ChatId AND MessageIdPg = ANY(@DeletedIds)
                RETURNING MessageId
            )
            UPDATE TrackedMessages
            SET NextDeleteAttemptAt = GREATEST(NextDeleteAttemptAt, @NextAttemptAt)
            WHERE ChatId = @ChatId AND MessageIdPg = ANY(@DeferredIds)";

        internal const string GetByChat = @"
            SELECT MessageIdPg FROM TrackedMessages
            WHERE ChatId = @ChatId AND Kind <> @CompletionKind
            ORDER BY CreatedAt ASC";

        internal const string GetByChatAndKind = @"
            SELECT MessageIdPg FROM TrackedMessages
            WHERE ChatId = @ChatId AND Kind = @Kind
            ORDER BY CreatedAt ASC";

        internal const string GetForCleanup = @"
            SELECT t.ChatId, t.MessageIdPg AS MessageId
            FROM TrackedMessages t
            LEFT JOIN NotificationOutbox n ON t.Kind = @JobStatusKind
                AND n.SessionId = t.SessionId AND n.EventType = 'session_completed' AND n.Status = 'sent'
            WHERE (t.DeleteAfter <= NOW() OR (t.DeleteAfter IS NULL AND t.CreatedAt <= @OlderThan)
                OR n.OutboxId IS NOT NULL)
              AND t.NextDeleteAttemptAt <= NOW()
              AND t.CreatedAt > @NewerThan
            ORDER BY GREATEST(LEAST(COALESCE(t.DeleteAfter, t.CreatedAt + @Retention), n.SentAt),
                t.NextDeleteAttemptAt), t.MessageId
            LIMIT @Limit";

        internal const string DeleteOlderThan = @"
            DELETE FROM TrackedMessages
            WHERE CreatedAt <= @OlderThan";
    }
}
