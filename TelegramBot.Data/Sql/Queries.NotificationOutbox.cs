namespace TelegramBot.Data;

internal static partial class SqlQueries
{
    internal static class NotificationOutbox
    {
        internal const string ClaimPending = @"
            WITH candidates AS (
                SELECT n.OutboxId
                FROM NotificationOutbox n
                WHERE n.EventType IN ('session_started', 'session_completed')
                  AND n.NextAttemptAt <= NOW()
                  AND (
                      Status = 'pending'
                      OR (Status = 'processing' AND LockedUntil < NOW())
                  )
                ORDER BY CreatedAt, OutboxId
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            ),
            claimed AS (
                UPDATE NotificationOutbox n
                SET Status = 'processing',
                    Attempts = Attempts + 1,
                    LockedUntil = NOW() + (@LeaseSeconds * INTERVAL '1 second'),
                    UpdatedAt = NOW()
                FROM candidates c
                WHERE n.OutboxId = c.OutboxId
                RETURNING n.OutboxId, n.SessionId, n.CorrelationId, n.EventType, n.Attempts
            )
            SELECT c.*, s.UserId
            FROM claimed c JOIN Sessions s ON s.SessionId = c.SessionId
            ORDER BY c.OutboxId;";

        // Runs before claiming: an obsolete start must never appear after the final result.
        internal const string SuppressObsolete = @"
            UPDATE NotificationOutbox n
            SET Status = 'failed', LockedUntil = NULL, UpdatedAt = NOW(),
                LastError = 'Notification superseded by session completion or deletion'
            FROM Sessions s
            WHERE n.SessionId = s.SessionId
              AND n.Status IN ('pending', 'processing')
              AND (s.Status = 'Deleted' OR (n.EventType = 'session_started' AND NOT EXISTS (
                  SELECT 1 FROM Commands c WHERE c.SessionId = n.SessionId AND c.Status IN ('pending', 'processing'))));";

        internal const string GetMissingCompletions = @"
            SELECT s.SessionId, s.CorrelationId
            FROM Sessions s
            WHERE s.Status != 'Deleted'
              AND EXISTS (SELECT 1 FROM Commands c WHERE c.SessionId = s.SessionId AND c.Status IN ('Done', 'Failed'))
              AND NOT EXISTS (SELECT 1 FROM Commands c WHERE c.SessionId = s.SessionId AND c.Status IN ('pending', 'processing'))
              AND NOT EXISTS (SELECT 1 FROM NotificationOutbox n WHERE n.SessionId = s.SessionId AND n.EventType = 'session_completed')
            ORDER BY s.SessionId LIMIT 100;";

        // Unique index on session_completed blocks a second INSERT; revive failed rows instead.
        internal const string RequeueFailedCompletions = @"
            UPDATE NotificationOutbox n
            SET Status = 'pending',
                NextAttemptAt = NOW(),
                LockedUntil = NULL,
                UpdatedAt = NOW()
            FROM Sessions s
            WHERE n.SessionId = s.SessionId
              AND n.EventType = 'session_completed'
              AND n.Status = 'failed'
              AND n.UpdatedAt < NOW() - INTERVAL '15 minutes'
              AND s.Status != 'Deleted'
              AND EXISTS (SELECT 1 FROM Commands c WHERE c.SessionId = s.SessionId AND c.Status IN ('Done', 'Failed'))
              AND NOT EXISTS (
                  SELECT 1 FROM Commands c
                  WHERE c.SessionId = s.SessionId
                    AND c.Status IN ('pending', 'processing')
              );";

        internal const string MarkSent = @"
            UPDATE NotificationOutbox
            SET Status = 'sent',
                SentAt = NOW(),
                LockedUntil = NULL,
                LastError = NULL,
                UpdatedAt = NOW()
            WHERE OutboxId = @OutboxId
              AND Status IN ('processing', 'sent');";

        internal const string TrackDeliveredMessage = @"
            INSERT INTO TrackedMessages (SessionId, ChatId, MessageIdPg, CreatedAt, Kind, DeleteAfter)
            VALUES (@SessionId, @ChatId, @MessageId, @SentAt, @Kind, @DeleteAfter)
            ON CONFLICT (ChatId, MessageIdPg) DO NOTHING;";

        internal const string ExpireJobMessages = @"
            UPDATE TrackedMessages SET DeleteAfter = LEAST(DeleteAfter, NOW())
            WHERE SessionId = @SessionId AND Kind = @JobStatusKind;";

        internal const string MarkFailed = @"
            UPDATE NotificationOutbox
            SET Status = CASE WHEN @Permanent THEN 'failed' ELSE 'pending' END,
                NextAttemptAt = NOW() + (@RetryDelaySeconds * INTERVAL '1 second'),
                LockedUntil = NULL,
                LastError = @LastError,
                UpdatedAt = NOW()
            WHERE OutboxId = @OutboxId
              AND Status = 'processing';";

        // Session-level advisory lock: mutual exclusion между репликами Server при drain'е outbox.
        // Освобождается явно через ReleaseSenderLock (или автоматически при разрыве соединения).
        internal const string TryAcquireSenderLock = "SELECT pg_try_advisory_lock(@LockId);";

        internal const string ReleaseSenderLock = "SELECT pg_advisory_unlock(@LockId);";
    }
}
