using TelegramBot.Core.Constants;

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
              AND s.UserId = @UserId
              AND c.Status != 'Deleted';";

        internal const string CountActive = @"
            SELECT COUNT(*)
            FROM Commands c
            JOIN Sessions s ON s.SessionId = c.SessionId
            WHERE c.SessionId = @SessionId
              AND c.Status != 'Deleted';";

        internal const string SoftDelete = @"
            UPDATE Commands c
            SET Status = 'Deleted'
            FROM Sessions s
            WHERE c.CommandId = @CommandId
              AND s.SessionId = c.SessionId
              AND s.UserId = @UserId
              AND c.Status NOT IN ('Deleted', 'processing');";

        internal const string SoftDeleteBySession = @"
            UPDATE Commands SET Status = 'Deleted'
            WHERE SessionId = @SessionId
              AND Status NOT IN ('Deleted', 'processing');";

        internal const string SoftDeleteBySessionAndType = @"
            UPDATE Commands c
            SET Status = 'Deleted'
            FROM Sessions s
            WHERE c.SessionId = @SessionId
              AND s.SessionId = c.SessionId
              AND s.UserId = @UserId
              AND c.CommandText = @CommandType
              AND c.Status NOT IN ('Deleted', 'processing');";

        internal const string SoftDeleteLegacyCancelled =
            "UPDATE Commands SET Status = 'Deleted' WHERE Status = 'Cancelled';";

        internal const string GetSessionIdByCommandId = @"
            SELECT c.SessionId
            FROM Commands c
            JOIN Sessions s ON s.SessionId = c.SessionId
            WHERE c.CommandId = @CommandId
              AND s.UserId = @UserId
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
              AND Status = 'processing'
              AND Lease = @ClaimedLease;";

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
                RETURNING s.SessionId, s.CorrelationId
            ),
            notified AS (
                INSERT INTO NotificationOutbox (EventType, SessionId, CorrelationId)
                SELECT 'session_started', SessionId, CorrelationId
                FROM session_marked
                ON CONFLICT DO NOTHING
                RETURNING OutboxId
            )
            SELECT COUNT(*)::int FROM notified;";

        internal static readonly string ClaimAndReturn = $@"
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
                  AND pg_try_advisory_xact_lock({AdvisoryLockIds.PartitionClaim}, hashtext(p.Partition))
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
                      selected.Priority, selected.RetryCount, c.Lease;";

        internal const string ReturnToPending = @"
            UPDATE Commands
            SET Status = 'pending',
                Lease = NULL,
                StartedAt = NULL,
                RetryCount = CASE WHEN @IncrementRetry THEN COALESCE(RetryCount, 0) + 1 ELSE RetryCount END,
                NextRetryAt = @NextRetryAt,
                ErrorMessage = @ErrorMessage
            WHERE CommandId = @CommandId
              AND Status = 'processing'
              AND Lease = @ClaimedLease
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
              AND SessionId = @SessionId
              AND Lease IS NOT NULL
              AND Lease < @CurrentTimeSec;";

        internal const string GetExpiredLeaseSessions = @"
            SELECT DISTINCT c.SessionId, s.CorrelationId
            FROM Commands c JOIN Sessions s ON s.SessionId = c.SessionId
            WHERE c.Status = 'processing' AND c.Lease < @CurrentTimeSec
            ORDER BY c.SessionId;";

        internal const string CountPendingProcessingBySession = @"
            SELECT COUNT(*)
            FROM Commands
            WHERE SessionId = @SessionId
              AND Status IN ('pending', 'processing')";

        // Transaction-scoped lock namespace for one terminal transition and its session completion
        // notification.  It serializes completions in a session before the active-command count is read.
        internal static readonly string AcquireSessionCompletionLock =
            $"SELECT pg_advisory_xact_lock({AdvisoryLockIds.SessionCompletion}, @SessionId);";

        // Все не-Deleted строки сессии. Warning плагина — Status=Done и непустой ErrorMessage (см. SessionCompletionSummary.Warned).
        internal const string GetCommandsForCompletion = @"
            SELECT FilePath, RootPath, CommandText, Status, ErrorMessage
            FROM Commands
            WHERE SessionId = @SessionId
              AND Status != 'Deleted'
            ORDER BY ExecutionOrder, CommandId";

    }
}
