using TelegramBot.Core.Constants;

namespace TelegramBot.Data;

internal static partial class SqlQueries
{
    internal static class Sessions
    {
        internal const string Insert = @"
            INSERT INTO Sessions (UserId, Username, CorrelationId, ProjectName, FilesAmount)
            VALUES (@UserId, @Username, @CorrelationId, @ProjectName, @FilesAmount)
            RETURNING SessionId;";

        internal static readonly string AcquireUserQueueLock =
            $"SELECT pg_advisory_xact_lock({AdvisoryLockIds.UserQueue}, hashtext(CAST(@UserId AS text)));";

        internal const string UpdateFilesAmount = @"
            UPDATE Sessions SET FilesAmount = @FilesAmount WHERE SessionId = @SessionId;";

        internal const string GetListFiltered = @"
            WITH session_stats AS (
                SELECT
                    s.SessionId,
                    COUNT(c.CommandId) FILTER (WHERE c.Status = 'Done') AS DoneCount,
                    COUNT(c.CommandId) FILTER (WHERE c.Status = 'Failed') AS FailedCount,
                    COUNT(c.CommandId) FILTER (WHERE c.Status IN ('pending', 'processing')) AS ActiveCount
                FROM Sessions s
                LEFT JOIN Commands c ON c.SessionId = s.SessionId AND c.Status != 'Deleted'
                WHERE s.Status != 'Deleted'
                  AND s.UserId = @UserId
                GROUP BY s.SessionId
            )
            SELECT
                s.SessionId,
                s.Username,
                s.ProjectName,
                s.CreatedAt AS Date,
                COALESCE(stats.DoneCount, 0) + COALESCE(stats.FailedCount, 0) + COALESCE(stats.ActiveCount, 0) AS TotalCommands,
                COALESCE(stats.DoneCount, 0) AS DoneCommands,
                COALESCE(stats.FailedCount, 0) AS FailedCommands
            FROM Sessions s
            LEFT JOIN session_stats stats ON stats.SessionId = s.SessionId
            WHERE s.Status != 'Deleted'
              AND s.UserId = @UserId
              AND (@Filter = 'ALL'
                   OR (@Filter = 'ACTIVE' AND COALESCE(stats.ActiveCount, 0) > 0)
                   OR (@Filter = 'DONE' AND COALESCE(stats.ActiveCount, 0) = 0 AND COALESCE(stats.FailedCount, 0) = 0 AND COALESCE(stats.DoneCount, 0) > 0)
                   OR (@Filter = 'FAILED' AND COALESCE(stats.FailedCount, 0) > 0))
            ORDER BY s.CreatedAt DESC;";

        internal const string CountQueuedFilesByUserSince = @"
            SELECT COALESCE(SUM(FilesAmount), 0)::int
            FROM Sessions
            WHERE UserId = @UserId
              AND Status != 'Deleted'
              AND CreatedAt >= @SinceUtc;";

        internal const string GetUsername = @"
            SELECT Username
            FROM Sessions
            WHERE SessionId = @SessionId;";

        internal const string GetStatus = @"
            SELECT s.Status, s.ProjectName, s.CreatedAt
            FROM Sessions s
            WHERE s.SessionId = @SessionId
              AND s.UserId = @UserId
              AND s.Status != 'Deleted';";

        internal const string GetCompletionSummary = @"
            SELECT
                s.UserId,
                s.Username,
                s.ProjectName,
                COUNT(CASE WHEN c.Status != 'Deleted' THEN 1 END)::int AS TotalFiles,
                COUNT(CASE WHEN c.Status = 'Done' THEN 1 END)::int AS DoneFiles,
                COUNT(CASE WHEN c.Status = 'Failed' THEN 1 END)::int AS FailedFiles,
                EXTRACT(EPOCH FROM (MAX(c.CompletedAt) - MIN(c.StartedAt)))::int AS DurationSeconds
            FROM Sessions s
            LEFT JOIN Commands c ON c.SessionId = s.SessionId AND c.Status != 'Deleted'
            WHERE s.SessionId = @SessionId
            GROUP BY s.SessionId;";

        internal const string SoftDelete = @"
            UPDATE Sessions SET Status = 'Deleted'
            WHERE SessionId = @SessionId
              AND UserId = @UserId
              AND Status != 'Deleted'
              AND NOT EXISTS (
                  SELECT 1 FROM Commands c
                  WHERE c.SessionId = @SessionId
                    AND c.Status = 'processing'
              );";

        internal const string SoftDeleteInactiveOlderThan = @"
            WITH deleted_sessions AS (
                UPDATE Sessions s
                SET Status = 'Deleted',
                    UpdatedAt = NOW()
                WHERE s.Status != 'Deleted'
                  AND s.CreatedAt < @CutoffUtc
                  AND NOT EXISTS (
                      SELECT 1 FROM NotificationOutbox n WHERE n.SessionId = s.SessionId
                        AND n.EventType = 'session_completed' AND n.Status != 'sent'
                  )
                  AND NOT EXISTS (
                      SELECT 1
                      FROM Commands c
                      WHERE c.SessionId = s.SessionId
                        AND c.Status IN ('pending', 'processing')
                  )
                RETURNING s.SessionId
            ),
            deleted_commands AS (
                UPDATE Commands c
                SET Status = 'Deleted'
                FROM deleted_sessions ds
                WHERE c.SessionId = ds.SessionId
                  AND c.Status != 'Deleted'
                RETURNING c.CommandId
            )
            SELECT COUNT(*)::int FROM deleted_sessions;";

        internal const string NotifyCompletionOnce = @"
            WITH marked AS (
                UPDATE Sessions
                SET CompletionNotified = TRUE,
                    UpdatedAt = NOW()
                WHERE SessionId = @SessionId
                  AND Status != 'Deleted'
                  AND EXISTS (SELECT 1 FROM Commands c WHERE c.SessionId = @SessionId AND c.Status IN ('Done', 'Failed'))
                  AND NOT EXISTS (SELECT 1 FROM Commands c WHERE c.SessionId = @SessionId AND c.Status IN ('pending', 'processing'))
                  AND NOT EXISTS (SELECT 1 FROM NotificationOutbox n WHERE n.SessionId = @SessionId AND n.EventType = 'session_completed')
                RETURNING SessionId
            ),
            outbox AS (
                INSERT INTO NotificationOutbox (EventType, SessionId, CorrelationId)
                SELECT 'session_completed', SessionId, @CorrelationId
                FROM marked
                ON CONFLICT DO NOTHING
                RETURNING OutboxId
            )
            SELECT COUNT(*)::int FROM outbox;";
    }
}
