namespace TelegramBot.Data;

internal static partial class SqlQueries
{
    internal static class Sessions
    {
        internal const string Insert = @"
            INSERT INTO Sessions (UserId, Username, FilesAmount)
            VALUES (@UserId, @Username, @FilesAmount)
            RETURNING SessionId;";

        internal const string GetList = @"
            SELECT SessionId, CreatedAt AS Date
            FROM Sessions
            WHERE UserId = @UserId AND Status != 'Deleted'
            ORDER BY CreatedAt DESC
            LIMIT 20;";

        internal const string GetStatus = @"
            SELECT
                s.Status,
                COUNT(CASE WHEN c.Status != 'Deleted' THEN 1 END) AS TotalFiles,
                COUNT(CASE WHEN c.Status = 'Done'     THEN 1 END) AS DoneFiles
            FROM Sessions s
            LEFT JOIN Commands c ON c.SessionId = s.SessionId
            WHERE s.SessionId = @SessionId
              AND s.UserId = @UserId
            GROUP BY s.Status;";

        internal const string SoftDelete = @"
            UPDATE Sessions SET Status = 'Deleted'
            WHERE SessionId = @SessionId AND UserId = @UserId;";
    }
}
