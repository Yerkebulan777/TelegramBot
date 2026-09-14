namespace TelegramBot.Data;

internal static partial class SqlQueries
{
    internal static class Schema
    {
        internal const string CreateSessionsTable = @"
            CREATE TABLE IF NOT EXISTS Sessions (
                SessionId SERIAL PRIMARY KEY,
                UserId BIGINT NOT NULL,
                Username TEXT,
                CorrelationId TEXT NOT NULL,
                Status TEXT NOT NULL DEFAULT 'pending',
                ProjectName TEXT,
                CompletionNotified BOOLEAN NOT NULL DEFAULT FALSE,
                StartNotified BOOLEAN NOT NULL DEFAULT FALSE,
                CreatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                FilesAmount INTEGER,
                UpdatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );";

        internal const string EnsureSessionsColumns = @"
            ALTER TABLE Sessions
            ADD COLUMN IF NOT EXISTS CorrelationId TEXT,
            ADD COLUMN IF NOT EXISTS Status TEXT NOT NULL DEFAULT 'pending',
            ADD COLUMN IF NOT EXISTS ProjectName TEXT,
            ADD COLUMN IF NOT EXISTS CompletionNotified BOOLEAN NOT NULL DEFAULT FALSE,
            ADD COLUMN IF NOT EXISTS StartNotified BOOLEAN NOT NULL DEFAULT FALSE,
            ADD COLUMN IF NOT EXISTS FilesAmount INTEGER,
            ADD COLUMN IF NOT EXISTS UpdatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW();

            UPDATE Sessions
            SET CorrelationId = 'legacy-' || SessionId
            WHERE CorrelationId IS NULL;

            ALTER TABLE Sessions
            ALTER COLUMN CorrelationId SET NOT NULL;";

        internal const string CreateCommandsTable = @"
            CREATE TABLE IF NOT EXISTS Commands (
                CommandId SERIAL PRIMARY KEY,
                SessionId INTEGER NOT NULL REFERENCES Sessions(SessionId),
                CommandText TEXT NOT NULL,
                FilePath TEXT,
                ExecutionOrder INTEGER NOT NULL,
                Status TEXT NOT NULL DEFAULT 'pending',
                CreatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                StartedAt TIMESTAMPTZ,
                CompletedAt TIMESTAMPTZ,
                Lease BIGINT,
                Partition TEXT,
                Priority INTEGER NOT NULL DEFAULT 5,
                ProcessId INTEGER,
                ErrorMessage TEXT,
                RetryCount INTEGER NOT NULL DEFAULT 0,
                NextRetryAt TIMESTAMPTZ,
                UpdatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );";

        internal const string EnsureCommandsColumns = @"
            ALTER TABLE Commands
            ADD COLUMN IF NOT EXISTS Lease BIGINT,
            ADD COLUMN IF NOT EXISTS Partition TEXT,
            ADD COLUMN IF NOT EXISTS Priority INTEGER NOT NULL DEFAULT 5,
            ADD COLUMN IF NOT EXISTS ProcessId INTEGER,
            ADD COLUMN IF NOT EXISTS ErrorMessage TEXT,
            ADD COLUMN IF NOT EXISTS RetryCount INTEGER NOT NULL DEFAULT 0,
            ADD COLUMN IF NOT EXISTS NextRetryAt TIMESTAMPTZ,
            ADD COLUMN IF NOT EXISTS UpdatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            ADD COLUMN IF NOT EXISTS RootPath TEXT;

            ALTER TABLE Commands
            ALTER COLUMN Lease TYPE BIGINT,
            ALTER COLUMN Priority SET DEFAULT 5,
            ALTER COLUMN RetryCount SET DEFAULT 0;

            UPDATE Commands
            SET Partition = 'file:' || md5(lower(COALESCE(NULLIF(FilePath, ''), CommandText)))
            WHERE Partition IS NULL;";

        internal const string CreateTrackedMessagesTable = @"
            CREATE TABLE IF NOT EXISTS TrackedMessages (
                MessageId SERIAL PRIMARY KEY,
                SessionId INTEGER REFERENCES Sessions(SessionId),
                ChatId BIGINT NOT NULL,
                MessageIdPg INTEGER NOT NULL,
                CreatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );";

        internal const string CreateRuntimeSettingsTable = @"
            CREATE TABLE IF NOT EXISTS RuntimeSettings (
                SettingKey TEXT PRIMARY KEY,
                SettingValue TEXT NOT NULL,
                UpdatedByUserId BIGINT,
                UpdatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );";

        // Одна строка на продукт (Revit, AutoCad); строки создаёт сам gate через upsert.
        // RevitLaunchState — предшественник с единственной singleton-строкой, хранил только cooldown.
        internal const string CreateProcessLaunchStateTable = @"
            CREATE TABLE IF NOT EXISTS ProcessLaunchState (
                Product TEXT PRIMARY KEY,
                LastLaunchAt TIMESTAMPTZ
            );

            DROP TABLE IF EXISTS RevitLaunchState;";

        internal const string CreateNotificationOutboxTable = @"
            CREATE TABLE IF NOT EXISTS NotificationOutbox (
                OutboxId BIGSERIAL PRIMARY KEY,
                EventType TEXT NOT NULL,
                SessionId INTEGER NOT NULL REFERENCES Sessions(SessionId),
                CorrelationId TEXT NOT NULL,
                Status TEXT NOT NULL DEFAULT 'pending',
                Attempts INTEGER NOT NULL DEFAULT 0,
                NextAttemptAt TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                LockedUntil TIMESTAMPTZ,
                LastError TEXT,
                CreatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UpdatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                SentAt TIMESTAMPTZ
            );";

        internal const string MakeTrackedMessagesSessionNullable = @"
            ALTER TABLE TrackedMessages
            ALTER COLUMN SessionId DROP NOT NULL;";

        internal const string EnsureTrackedMessageLifecycle = @"
            ALTER TABLE TrackedMessages
                ADD COLUMN IF NOT EXISTS Kind TEXT NOT NULL DEFAULT 'completion',
                ADD COLUMN IF NOT EXISTS DeleteAfter TIMESTAMPTZ,
                ADD COLUMN IF NOT EXISTS NextDeleteAttemptAt TIMESTAMPTZ NOT NULL DEFAULT NOW();

            -- Legacy rows have no reliable message kind. Protect them until normal retention
            -- rather than accidentally deleting a fresh completion on the first interaction.
            ALTER TABLE TrackedMessages ALTER COLUMN Kind SET DEFAULT 'interface';

            DELETE FROM TrackedMessages duplicate
            USING TrackedMessages original
            WHERE duplicate.ChatId = original.ChatId
              AND duplicate.MessageIdPg = original.MessageIdPg
              AND duplicate.MessageId > original.MessageId;

            CREATE UNIQUE INDEX IF NOT EXISTS idx_tracked_messages_identity
                ON TrackedMessages(ChatId, MessageIdPg);
            CREATE INDEX IF NOT EXISTS idx_tracked_messages_due
                ON TrackedMessages(NextDeleteAttemptAt, DeleteAfter);";

        internal const string EnsureNotificationOutboxColumns = @"
            ALTER TABLE NotificationOutbox
            ADD COLUMN IF NOT EXISTS EventType TEXT,
            ADD COLUMN IF NOT EXISTS SessionId INTEGER,
            ADD COLUMN IF NOT EXISTS CorrelationId TEXT,
            ADD COLUMN IF NOT EXISTS Status TEXT NOT NULL DEFAULT 'pending',
            ADD COLUMN IF NOT EXISTS Attempts INTEGER NOT NULL DEFAULT 0,
            ADD COLUMN IF NOT EXISTS NextAttemptAt TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            ADD COLUMN IF NOT EXISTS LockedUntil TIMESTAMPTZ,
            ADD COLUMN IF NOT EXISTS LastError TEXT,
            ADD COLUMN IF NOT EXISTS CreatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            ADD COLUMN IF NOT EXISTS UpdatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            ADD COLUMN IF NOT EXISTS SentAt TIMESTAMPTZ;";

        internal const string CreateIndexes = @"
            CREATE INDEX IF NOT EXISTS idx_commands_status ON Commands(Status);
            CREATE INDEX IF NOT EXISTS idx_commands_session ON Commands(SessionId);
            CREATE INDEX IF NOT EXISTS idx_commands_status_lease ON Commands(Status, Lease)
                WHERE Status = 'processing';
            CREATE INDEX IF NOT EXISTS idx_sessions_user_created ON Sessions(UserId, CreatedAt DESC);
            CREATE INDEX IF NOT EXISTS idx_sessions_correlation_id ON Sessions(CorrelationId);
            DROP INDEX IF EXISTS idx_commands_pending_priority;
            CREATE INDEX IF NOT EXISTS idx_commands_pending_priority ON Commands(Status, Priority ASC, CreatedAt ASC, CommandId ASC)
                WHERE Status = 'pending';
            CREATE INDEX IF NOT EXISTS idx_commands_partition_status ON Commands(Partition, Status);
            CREATE INDEX IF NOT EXISTS idx_commands_processing_partition ON Commands(Partition)
                WHERE Status = 'processing';
            CREATE INDEX IF NOT EXISTS idx_commands_claim_partition ON Commands(Status, Partition, Priority ASC, CreatedAt ASC, CommandId ASC)
                WHERE Status = 'pending';
            CREATE UNIQUE INDEX IF NOT EXISTS idx_commands_unique ON Commands(SessionId, CommandText, FilePath);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_commands_active_unique ON Commands(CommandText, FilePath)
                WHERE Status IN ('pending', 'processing');
            CREATE INDEX IF NOT EXISTS idx_tracked_messages_session ON TrackedMessages(SessionId);
            CREATE INDEX IF NOT EXISTS idx_tracked_messages_chat ON TrackedMessages(ChatId);
            CREATE INDEX IF NOT EXISTS idx_tracked_messages_created_at ON TrackedMessages(CreatedAt);
            CREATE INDEX IF NOT EXISTS idx_commands_updated_at ON Commands(UpdatedAt DESC);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_notification_outbox_session_completed
                ON NotificationOutbox(EventType, SessionId)
                WHERE EventType = 'session_completed';
            CREATE UNIQUE INDEX IF NOT EXISTS idx_notification_outbox_session_started
                ON NotificationOutbox(EventType, SessionId)
                WHERE EventType = 'session_started';
            CREATE INDEX IF NOT EXISTS idx_notification_outbox_pending
                ON NotificationOutbox(Status, NextAttemptAt, CreatedAt, OutboxId)
                WHERE Status IN ('pending', 'processing');";

        // CHECK-constraints на допустимые значения Status. Статусы в коде — смешанного регистра
        // ('pending'/'processing' lowercase, 'Done'/'Failed'/'Deleted' PascalCase), SQL-сравнения строгие —
        // опечатка регистра в новом запросе даёт молча пустой результат. CHECK ловит такие ошибки на
        // insert/update, а не при последующем SELECT. Idempotent через DO/EXCEPTION (в PG нет
        // ADD CONSTRAINT IF NOT EXISTS). Каждая константа — ровно один стейтмент (Dapper ExecuteAsync
        // выполняет одну команду за вызов).

        internal const string AddCommandsStatusCheck = @"
            DO $$ BEGIN
                ALTER TABLE Commands
                    ADD CONSTRAINT chk_commands_status
                    CHECK (Status IN ('pending', 'processing', 'Done', 'Failed', 'Deleted'));
            EXCEPTION WHEN duplicate_object THEN NULL; END $$;";

        internal const string AddSessionsStatusCheck = @"
            DO $$ BEGIN
                ALTER TABLE Sessions
                    ADD CONSTRAINT chk_sessions_status
                    CHECK (Status IN ('pending', 'Deleted'));
            EXCEPTION WHEN duplicate_object THEN NULL; END $$;";

        internal const string AddNotificationOutboxStatusCheck = @"
            DO $$ BEGIN
                ALTER TABLE NotificationOutbox
                    ADD CONSTRAINT chk_notification_outbox_status
                    CHECK (Status IN ('pending', 'processing', 'sent', 'failed'));
            EXCEPTION WHEN duplicate_object THEN NULL; END $$;";
    }
}
