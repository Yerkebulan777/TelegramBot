namespace TelegramBot.Data;

internal static partial class SqlQueries
{
    internal static class Commands
    {
        internal const string GetActiveConflicts = @"
            SELECT c.CommandText AS Command, c.FilePath, c.CommandId, c.SessionId, c.Status, c.CreatedAt
            FROM Commands c
            JOIN unnest(@CommandTexts::text[], @FilePaths::text[]) AS requested(CommandText, FilePath)
              ON c.CommandText = requested.CommandText AND c.FilePath = requested.FilePath
            WHERE c.Status IN ('pending', 'processing')
              AND c.SessionId != @SessionId;";

        internal const string InsertBatch = @"
            INSERT INTO Commands (SessionId, CommandText, FilePath, RootPath, ExecutionOrder, Priority, Partition)
            SELECT @SessionId,
                   data.CommandText,
                   data.FilePath,
                   @RootPath,
                   data.ExecutionOrder,
                   data.Priority,
                   'file:' || md5(lower(COALESCE(NULLIF(data.FilePath, ''), data.CommandText)))
            FROM unnest(
                @CommandTexts::text[],
                @FilePaths::text[],
                @Orders::int[],
                @Priorities::int[]
            ) AS data(CommandText, FilePath, ExecutionOrder, Priority)
            ON CONFLICT (CommandText, FilePath) WHERE Status IN ('pending', 'processing') DO NOTHING
            RETURNING CommandText, FilePath";

        internal const string GetBySession = @"
            SELECT c.CommandText AS Command, c.FilePath AS FileName, c.Status, c.CommandId
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
            WHERE CommandId = @CommandId;";

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
            LIMIT 1;";

        internal const string GetRerunSnapshot = @"
            SELECT c.CommandId AS SourceCommandId,
                   c.CommandText,
                   c.FilePath,
                   c.RootPath,
                   c.Priority,
                   s.ProjectName
            FROM Commands c
            JOIN Sessions s ON s.SessionId = c.SessionId
            WHERE c.CommandId = @CommandId
              AND c.Status IN ('Done', 'Failed')
              AND c.FilePath IS NOT NULL
              AND c.RootPath IS NOT NULL
              AND s.UserId = @UserId
              AND s.Status != 'Deleted'
            LIMIT 1;";

        internal const string UpdateStatus = @"
            UPDATE Commands
            SET Status = @Status,
                CompletedAt = CASE
                    WHEN @Status IN ('Done', 'Failed') THEN NOW()
                    ELSE CompletedAt
                END,
                ProcessId = @ProcessId,
                ErrorMessage = @ErrorMessage,
                Lease = NULL,
                NextRetryAt = NULL
            WHERE CommandId = @CommandId
              AND Status != 'Deleted';";

        internal const string MarkProcessStartedAndNotifyOnce = @"
            WITH command_updated AS (
                UPDATE Commands
                SET ProcessId = @ProcessId
                WHERE CommandId = @CommandId
                  AND Status = 'processing'
                RETURNING SessionId
            ),
            session_marked AS (
                UPDATE Sessions s
                SET StartNotified = TRUE,
                    UpdatedAt = NOW()
                FROM command_updated cu
                WHERE s.SessionId = cu.SessionId
                  AND s.StartNotified = FALSE
                  AND s.Status != 'Deleted'
                RETURNING s.SessionId
            ),
            notified AS (
                SELECT pg_notify('session_started', @Payload)
                FROM session_marked
            )
            SELECT COUNT(*)::int FROM notified;";

        internal const string ClaimAndReturn = @"
            WITH candidates AS (
                SELECT c.CommandId, c.SessionId, c.CommandText, c.FilePath, c.RootPath, c.ExecutionOrder,
                       s.UserId, s.Username, s.CorrelationId,
                       c.Partition,
                       c.Priority, c.RetryCount, c.CreatedAt
                FROM Commands c
                JOIN Sessions s ON s.SessionId = c.SessionId
                WHERE c.Status = 'pending'
                  AND s.Status != 'Deleted'
                  AND (c.NextRetryAt IS NULL OR c.NextRetryAt <= NOW())
                  AND c.Partition IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1
                      FROM Commands running
                      WHERE running.Status = 'processing'
                        AND running.Partition = c.Partition
                  )
            ),
            one_per_partition AS (
                SELECT DISTINCT ON (Partition)
                       CommandId, SessionId, CommandText, FilePath, RootPath, ExecutionOrder,
                       UserId, Username, CorrelationId, Partition,
                       Priority, RetryCount, CreatedAt
                FROM candidates
                ORDER BY Partition, Priority ASC, CreatedAt ASC, CommandId ASC
            ),
            selected AS (
                SELECT p.CommandId, p.SessionId, p.CommandText, p.FilePath, p.RootPath, p.ExecutionOrder,
                       p.UserId, p.Username, p.CorrelationId, p.Partition,
                       p.Priority, p.RetryCount
                FROM one_per_partition p
                JOIN Commands lockc ON lockc.CommandId = p.CommandId
                WHERE lockc.Status = 'pending'
                  AND pg_try_advisory_xact_lock(1234568, hashtext(p.Partition))
                ORDER BY p.Priority ASC, p.CreatedAt ASC, p.CommandId ASC
                LIMIT @Limit
                FOR UPDATE OF lockc SKIP LOCKED
            )
            UPDATE Commands c
            SET Status = 'processing',
                Lease = @LeaseExpiry,
                StartedAt = NOW()
            FROM selected
            WHERE c.CommandId = selected.CommandId
              AND c.Status = 'pending'
            RETURNING selected.CommandId, selected.SessionId, selected.CommandText,
                      selected.FilePath, selected.RootPath, selected.ExecutionOrder, selected.UserId,
                      selected.Username, selected.CorrelationId,
                      selected.Partition,
                      selected.Priority, selected.RetryCount;";

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
            SET Status = CASE
                    -- Poison-command guard: при превышении MaxRetries считаем команду перманентно
                    -- невыполнимой (Worker падает, не записав статус) — фиксируем Failed, иначе цикл
                    -- «claim → crash → lease expiry → pending» длится бесконечно, а MaxRetries из обычного
                    -- retry-пути (ScheduleRetry) этого сценария не покрывает.
                    WHEN RetryCount + 1 >= @MaxRetries THEN 'Failed'
                    ELSE 'pending'
                END,
                RetryCount = RetryCount + 1,
                Lease = NULL,
                StartedAt = NULL,
                CompletedAt = CASE WHEN RetryCount + 1 >= @MaxRetries THEN NOW() ELSE CompletedAt END,
                ErrorMessage = 'Lease expired: worker crash or timeout'
            WHERE Status = 'processing'
              AND Lease IS NOT NULL
              AND Lease < @CurrentTimeSec;";

        internal const string CountPendingProcessingBySession = @"
            SELECT COUNT(*)
            FROM Commands
            WHERE SessionId = @SessionId
              AND Status IN ('pending', 'processing')";

        // Transaction-scoped lock namespace for one terminal transition and its session completion
        // notification.  It serializes completions in a session before the active-command count is read.
        internal const string AcquireSessionCompletionLock = @"
            SELECT pg_advisory_xact_lock(1234570, @SessionId);";

        internal const string GetFailedCommandsBySession = @"
            SELECT FilePath, RootPath, CommandText, ErrorMessage
            FROM Commands
            WHERE SessionId = @SessionId
              AND Status = 'Failed'
            ORDER BY ExecutionOrder, CommandId";

        // Done rows reuse Commands.ErrorMessage for ResultFile.warningMessage (success with recoverable issues).
        // Always filter Status = 'Done' — never treat ErrorMessage alone as failure.
        internal const string GetWarnedCommandsBySession = @"
            SELECT FilePath, RootPath, CommandText, ErrorMessage
            FROM Commands
            WHERE SessionId = @SessionId
              AND Status = 'Done'
              AND ErrorMessage IS NOT NULL
              AND TRIM(ErrorMessage) <> ''
            ORDER BY ExecutionOrder, CommandId";

    }
}
