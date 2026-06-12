namespace TelegramBot.Data;

internal static partial class SqlQueries
{
    internal static class Sessions
    {
        internal const string Insert = @"
            INSERT INTO Sessions (UserId, Username, CorrelationId, ProjectName, FilesAmount)
            VALUES (@UserId, @Username, @CorrelationId, @ProjectName, @FilesAmount)
            RETURNING SessionId;";

        internal const string GetList = @"
            SELECT
                s.SessionId,
                s.UserId,
                s.Username,
                s.ProjectName,
                s.CreatedAt AS Date,
                s.Status,
                COUNT(c.CommandId) AS TotalCommands,
                COUNT(CASE WHEN c.Status = 'Done' THEN 1 END) AS DoneCommands,
                COUNT(CASE WHEN c.Status = 'Failed' THEN 1 END) AS FailedCommands,
                COUNT(CASE WHEN c.Status IN ('pending', 'processing') THEN 1 END) AS ActiveCommands
            FROM Sessions s
            LEFT JOIN Commands c ON c.SessionId = s.SessionId AND c.Status != 'Deleted'
            WHERE s.Status != 'Deleted'
            GROUP BY s.SessionId, s.UserId, s.Username, s.ProjectName, s.CreatedAt, s.Status
            ORDER BY s.CreatedAt DESC
            LIMIT 20;";

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
                GROUP BY s.SessionId
            )
            SELECT
                s.SessionId,
                s.UserId,
                s.Username,
                s.ProjectName,
                s.CreatedAt AS Date,
                s.Status,
                COALESCE(stats.DoneCount, 0) + COALESCE(stats.FailedCount, 0) + COALESCE(stats.ActiveCount, 0) AS TotalCommands,
                COALESCE(stats.DoneCount, 0) AS DoneCommands,
                COALESCE(stats.FailedCount, 0) AS FailedCommands,
                COALESCE(stats.ActiveCount, 0) AS ActiveCommands
            FROM Sessions s
            LEFT JOIN session_stats stats ON stats.SessionId = s.SessionId
            WHERE s.Status != 'Deleted'
              AND (@Filter = 'ALL'
                   OR (@Filter = 'ACTIVE' AND COALESCE(stats.ActiveCount, 0) > 0)
                   OR (@Filter = 'DONE' AND COALESCE(stats.ActiveCount, 0) = 0 AND COALESCE(stats.FailedCount, 0) = 0 AND COALESCE(stats.DoneCount, 0) > 0)
                   OR (@Filter = 'FAILED' AND COALESCE(stats.FailedCount, 0) > 0))
            ORDER BY s.CreatedAt DESC
            LIMIT @PageSize OFFSET @Offset;";

        internal const string CountFiltered = @"
            WITH session_stats AS (
                SELECT
                    s.SessionId,
                    COUNT(c.CommandId) FILTER (WHERE c.Status IN ('pending', 'processing')) AS ActiveCount,
                    COUNT(c.CommandId) FILTER (WHERE c.Status = 'Failed') AS FailedCount,
                    COUNT(c.CommandId) FILTER (WHERE c.Status = 'Done') AS DoneCount
                FROM Sessions s
                LEFT JOIN Commands c ON c.SessionId = s.SessionId AND c.Status != 'Deleted'
                WHERE s.Status != 'Deleted'
                GROUP BY s.SessionId
            )
            SELECT COUNT(*)::int
            FROM Sessions s
            LEFT JOIN session_stats stats ON stats.SessionId = s.SessionId
            WHERE s.Status != 'Deleted'
              AND (@Filter = 'ALL'
                   OR (@Filter = 'ACTIVE' AND COALESCE(stats.ActiveCount, 0) > 0)
                   OR (@Filter = 'DONE' AND COALESCE(stats.ActiveCount, 0) = 0 AND COALESCE(stats.FailedCount, 0) = 0 AND COALESCE(stats.DoneCount, 0) > 0)
                   OR (@Filter = 'FAILED' AND COALESCE(stats.FailedCount, 0) > 0));";

        internal const string CountQueuedFilesByUserSince = @"
            SELECT COALESCE(SUM(FilesAmount), 0)::int
            FROM Sessions
            WHERE UserId = @UserId
              AND Status != 'Deleted'
              AND CreatedAt >= @SinceUtc;";

        internal const string GetStatus = @"
            SELECT
                s.Status,
                s.CorrelationId,
                s.ProjectName,
                s.CreatedAt,
                COUNT(CASE WHEN c.Status != 'Deleted' THEN 1 END) AS TotalFiles,
                COUNT(CASE WHEN c.Status = 'Done'      THEN 1 END) AS DoneFiles,
                COUNT(CASE WHEN c.Status = 'Failed'    THEN 1 END) AS FailedFiles,
                COUNT(CASE WHEN c.Status = 'processing' THEN 1 END) AS ProcessingFiles,
                COUNT(CASE WHEN c.Status = 'pending'    THEN 1 END) AS PendingFiles
            FROM Sessions s
            LEFT JOIN Commands c ON c.SessionId = s.SessionId
            WHERE s.SessionId = @SessionId
            GROUP BY s.SessionId;";

        internal const string GetCompletionSummary = @"
            SELECT
                s.UserId,
                s.Username,
                s.SessionId,
                s.CorrelationId,
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
              AND (UserId = @UserId OR @IsAdmin = true);";

        internal const string SoftDeleteInactiveOlderThan = @"
            WITH deleted_sessions AS (
                UPDATE Sessions s
                SET Status = 'Deleted',
                    UpdatedAt = NOW()
                WHERE s.Status != 'Deleted'
                  AND s.CreatedAt < @CutoffUtc
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
                  AND CompletionNotified = FALSE
                  AND Status != 'Deleted'
                RETURNING SessionId
            ),
            notified AS (
                SELECT pg_notify('command_completed', @Payload)
                FROM marked
            )
            SELECT COUNT(*)::int FROM notified;";
    }
}
