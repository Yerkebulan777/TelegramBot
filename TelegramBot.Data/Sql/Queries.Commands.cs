namespace TelegramBot.Data;

internal static partial class SqlQueries
{
    internal static class Commands
    {
        internal const string Insert = @"
            INSERT INTO Commands (SessionId, CommandText, FilePath, ExecutionOrder)
            VALUES (@SessionId, @CommandText, @FilePath, @Order)";

        internal const string GetBySession = @"
            SELECT c.ExecutionOrder AS ExecOrder, c.CommandText AS Command,
                   c.FilePath AS FileName, c.Status, c.CreatedAt AS Date, c.CommandId
            FROM Commands c
            JOIN Sessions s ON s.SessionId = c.SessionId
            WHERE c.SessionId = @SessionId
              AND s.UserId = @UserId
              AND c.Status != 'Deleted';";

        internal const string CountActive = @"
            SELECT COUNT(*)
            FROM Commands c
            JOIN Sessions s ON s.SessionId = c.SessionId
            WHERE c.SessionId = @SessionId
              AND s.UserId = @UserId
              AND c.Status != 'Deleted';";

        internal const string SoftDelete = @"
            UPDATE Commands SET Status = 'Deleted'
            WHERE CommandId = @CommandId
              AND SessionId IN (SELECT SessionId FROM Sessions WHERE UserId = @UserId);";

        internal const string SoftDeleteBySession =
            "UPDATE Commands SET Status = 'Deleted' WHERE SessionId = @SessionId;";

        internal const string GetSessionIdByCommandId = @"
            SELECT c.SessionId
            FROM Commands c
            JOIN Sessions s ON s.SessionId = c.SessionId
            WHERE c.CommandId = @CommandId
              AND s.UserId = @UserId
              AND c.Status != 'Deleted'
              AND s.Status != 'Deleted'
            LIMIT 1;";

        internal const string GetPending = @"
            SELECT c.CommandId, c.SessionId, c.CommandText, c.FilePath, c.ExecutionOrder,
                   s.UserId, s.Username
            FROM Commands c
            JOIN Sessions s ON s.SessionId = c.SessionId
            WHERE c.Status = 'pending'
              AND s.Status != 'Deleted'
            ORDER BY c.SessionId, c.ExecutionOrder
            LIMIT @Limit;";

        internal const string UpdateStatus = @"
            UPDATE Commands SET Status = @Status WHERE CommandId = @CommandId;";

        internal const string ClaimAndReturn = @"
            WITH selected AS (
                SELECT c.CommandId, c.SessionId, c.CommandText, c.FilePath, c.ExecutionOrder,
                       s.UserId, s.Username
                FROM Commands c
                JOIN Sessions s ON s.SessionId = c.SessionId
                WHERE c.Status = 'pending'
                  AND s.Status != 'Deleted'
                ORDER BY c.SessionId, c.ExecutionOrder
                LIMIT @Limit
                FOR UPDATE SKIP LOCKED
            )
            UPDATE Commands c
            SET Status = 'processing', Lease = @LeaseExpiry
            FROM selected
            WHERE c.CommandId = selected.CommandId
            RETURNING selected.CommandId, selected.SessionId, selected.CommandText,
                      selected.FilePath, selected.ExecutionOrder, selected.UserId, selected.Username;";

        internal const string ReleaseExpiredLeases = @"
            UPDATE Commands
            SET Status = 'pending', Lease = NULL
            WHERE Status = 'processing'
              AND Lease IS NOT NULL
              AND Lease < @CurrentTimeSec;";
    }
}
