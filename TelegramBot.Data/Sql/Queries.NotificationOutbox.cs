namespace TelegramBot.Data;

internal static partial class SqlQueries
{
    internal static class NotificationOutbox
    {
        internal const string ClaimPending = @"
            WITH candidates AS (
                SELECT OutboxId
                FROM NotificationOutbox
                WHERE EventType = @EventType
                  AND NextAttemptAt <= NOW()
                  AND (
                      Status = 'pending'
                      OR (Status = 'processing' AND LockedUntil < NOW())
                  )
                ORDER BY CreatedAt, OutboxId
                LIMIT @Limit
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
                RETURNING n.OutboxId, n.EventType, n.SessionId, n.CorrelationId, n.Attempts
            )
            SELECT OutboxId, EventType, SessionId, CorrelationId, Attempts
            FROM claimed
            ORDER BY OutboxId;";

        internal const string MarkSent = @"
            UPDATE NotificationOutbox
            SET Status = 'sent',
                SentAt = NOW(),
                LockedUntil = NULL,
                LastError = NULL,
                UpdatedAt = NOW()
            WHERE OutboxId = @OutboxId
              AND Status = 'processing';";

        internal const string MarkFailed = @"
            UPDATE NotificationOutbox
            SET Status = CASE WHEN Attempts >= @MaxAttempts THEN 'failed' ELSE 'pending' END,
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
