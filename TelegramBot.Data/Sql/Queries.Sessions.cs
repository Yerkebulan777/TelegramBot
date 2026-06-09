namespace TelegramBot.Data;

internal static partial class SqlQueries
{
    internal static class Sessions
    {
        internal const string Insert = @"
            INSERT INTO Sessions (UserId, Username, ProjectName, FilesAmount)
            VALUES (@UserId, @Username, @ProjectName, @FilesAmount)
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

        internal const string CountQueuedFilesByUserSince = @"
            SELECT COALESCE(SUM(FilesAmount), 0)::int
            FROM Sessions
            WHERE UserId = @UserId
              AND Status != 'Deleted'
              AND CreatedAt >= @SinceUtc;";

        internal const string GetStatus = @"
            SELECT
                s.Status,
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
    }
}
