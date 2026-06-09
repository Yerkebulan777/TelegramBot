namespace TelegramBot.Data;

internal static partial class SqlQueries
{
    internal static class Commands
    {
        internal const string InsertBatch = @"
            INSERT INTO Commands (SessionId, CommandText, FilePath, ExecutionOrder, Priority)
            SELECT @SessionId, unnest(@CommandTexts::text[]), unnest(@FilePaths::text[]), unnest(@Orders::int[]), unnest(@Priorities::int[])";

        internal const string GetBySession = @"
            SELECT c.ExecutionOrder AS ExecOrder, c.CommandText AS Command,
                   c.FilePath AS FileName, c.Status, c.CreatedAt AS Date, c.CommandId
            FROM Commands c
            JOIN Sessions s ON s.SessionId = c.SessionId
            WHERE c.SessionId = @SessionId
              AND c.Status != 'Deleted';";

        internal const string CountActive = @"
            SELECT COUNT(*)
            FROM Commands c
            JOIN Sessions s ON s.SessionId = c.SessionId
            WHERE c.SessionId = @SessionId
              AND c.Status != 'Deleted';";

        internal const string SoftDelete = @"
            UPDATE Commands SET Status = 'Deleted'
            WHERE CommandId = @CommandId
              AND (SessionId IN (SELECT SessionId FROM Sessions WHERE UserId = @UserId)
                   OR @IsAdmin = true);";

        internal const string SoftDeleteBySession =
            "UPDATE Commands SET Status = 'Deleted' WHERE SessionId = @SessionId;";

        internal const string SoftDeleteBySessionAndType = @"
            UPDATE Commands SET Status = 'Deleted'
            WHERE SessionId = @SessionId
              AND CommandText = @CommandType
              AND Status NOT IN ('Deleted', 'processing');";

        internal const string SoftDeleteLegacyCancelled =
            "UPDATE Commands SET Status = 'Deleted' WHERE Status = 'Cancelled';";

        internal const string GetSessionIdByCommandId = @"
            SELECT c.SessionId
            FROM Commands c
            JOIN Sessions s ON s.SessionId = c.SessionId
            WHERE c.CommandId = @CommandId
              AND c.Status != 'Deleted'
              AND s.Status != 'Deleted'
              AND (s.UserId = @UserId OR @IsAdmin = true)
            LIMIT 1;";

        internal const string UpdateStatus = @"
            UPDATE Commands
            SET Status = @Status,
                CompletedAt = CASE 
                    WHEN @Status IN ('Done', 'Failed') THEN NOW() 
                    ELSE CompletedAt 
                END,
                ProcessId = @ProcessId,
                ErrorMessage = @ErrorMessage
            WHERE CommandId = @CommandId
              AND Status != 'Deleted';";


        internal const string ClaimAndReturn = @"
            WITH selected AS (
                SELECT c.CommandId, c.SessionId, c.CommandText, c.FilePath, c.ExecutionOrder,
                       s.UserId, s.Username, c.Partition, c.Priority, c.RetryCount
                FROM Commands c
                JOIN Sessions s ON s.SessionId = c.SessionId
                WHERE c.Status = 'pending'
                  AND s.Status != 'Deleted'
                  AND (c.NextRetryAt IS NULL OR c.NextRetryAt <= NOW())
                ORDER BY c.Priority ASC, c.CreatedAt ASC, c.CommandId ASC
                LIMIT @Limit
                FOR UPDATE SKIP LOCKED
            )
            UPDATE Commands c
            SET Status = 'processing', 
                Lease = @LeaseExpiry,
                StartedAt = NOW()
            FROM selected
            WHERE c.CommandId = selected.CommandId
            RETURNING selected.CommandId, selected.SessionId, selected.CommandText,
                      selected.FilePath, selected.ExecutionOrder, selected.UserId, 
                      selected.Username, selected.Partition, selected.Priority, selected.RetryCount;";

        internal const string ScheduleRetry = @"
            UPDATE Commands
            SET Status = 'pending',
                Lease = NULL,
                StartedAt = NULL,
                RetryCount = COALESCE(RetryCount, 0) + 1,
                NextRetryAt = @NextRetryAt,
                ErrorMessage = @ErrorMessage
            WHERE CommandId = @CommandId
            RETURNING RetryCount;";

        internal const string TryAdvisoryLock = "SELECT pg_try_advisory_lock(@LockId);";

        internal const string ReleaseAdvisoryLock = "SELECT pg_advisory_unlock(@LockId);";

        internal const string ReleaseExpiredLeases = @"
            UPDATE Commands
            SET Status = 'pending', 
                Lease = NULL,
                StartedAt = NULL,
                ErrorMessage = 'Lease expired: worker crash or timeout'
            WHERE Status = 'processing'
              AND Lease IS NOT NULL
              AND Lease < @CurrentTimeSec;";

        internal const string ReleaseTimeoutCommands = @"
            UPDATE Commands
            SET Status = 'pending',
                StartedAt = NULL,
                CompletedAt = NULL,
                ProcessId = NULL,
                ErrorMessage = 'Timeout: process exceeded maximum execution time',
                Lease = NULL
            WHERE Status = 'processing'
              AND StartedAt < NOW() - (@TimeoutSeconds || ' seconds')::INTERVAL;";

        internal const string GetById = @"
            SELECT c.CommandId, c.SessionId, c.CommandText, c.FilePath,
                   c.ExecutionOrder, s.UserId, s.Username, c.Partition, c.Priority, c.RetryCount
            FROM Commands c
            JOIN Sessions s ON s.SessionId = c.SessionId
            WHERE c.CommandId = @CommandId
              AND c.Status != 'Deleted'
              AND s.Status != 'Deleted'
              AND (s.UserId = @UserId OR @IsAdmin = true);";

        internal const string CountPendingProcessingBySession = @"
            SELECT COUNT(*)
            FROM Commands
            WHERE SessionId = @SessionId
              AND Status IN ('pending', 'processing')";

        internal const string CountDuplicatePairs = @"
            SELECT COUNT(*) FROM (
                SELECT unnest(@CommandTexts::text[]) AS cmd, unnest(@FilePaths::text[]) AS fpath
            ) input
            WHERE EXISTS (
                SELECT 1 FROM Commands c
                JOIN Sessions s ON s.SessionId = c.SessionId
                WHERE c.Status IN ('pending', 'processing')
                  AND s.Status != 'Deleted'
                  AND c.CommandText = input.cmd
                  AND c.FilePath = input.fpath
            )";
    }
}
