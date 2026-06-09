namespace TelegramBot.Data;

internal static partial class SqlQueries
{
    internal static class Schema
    {
        internal const string CreateBotUsersTable = @"
            CREATE TABLE IF NOT EXISTS BotUsers (
                UserId BIGINT PRIMARY KEY,
                Username TEXT,
                Role INTEGER NOT NULL DEFAULT 0,
                Status INTEGER NOT NULL DEFAULT 0,
                CreatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UpdatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );";

        internal const string CreateSessionsTable = @"
            CREATE TABLE IF NOT EXISTS Sessions (
                SessionId SERIAL PRIMARY KEY,
                UserId BIGINT NOT NULL,
                Username TEXT,
                PriorityId INTEGER NOT NULL DEFAULT 0,
                Status TEXT NOT NULL DEFAULT 'pending',
                ProjectName TEXT,
                CreatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                FilesAmount INTEGER,
                UpdatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );";

        internal const string EnsureSessionsColumns = @"
            ALTER TABLE Sessions
            ADD COLUMN IF NOT EXISTS PriorityId INTEGER NOT NULL DEFAULT 0,
            ADD COLUMN IF NOT EXISTS Status TEXT NOT NULL DEFAULT 'pending',
            ADD COLUMN IF NOT EXISTS ProjectName TEXT,
            ADD COLUMN IF NOT EXISTS FilesAmount INTEGER,
            ADD COLUMN IF NOT EXISTS UpdatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW();";

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
                GUID TEXT,
                Lease INTEGER,
                Partition TEXT,
                Priority INTEGER NOT NULL DEFAULT 50,
                ProcessId INTEGER,
                ErrorMessage TEXT,
                RetryCount INTEGER NOT NULL DEFAULT 0,
                NextRetryAt TIMESTAMPTZ,
                Progress INTEGER NOT NULL DEFAULT 0,
                Result TEXT,
                UpdatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );";

        internal const string EnsureCommandsColumns = @"
            ALTER TABLE Commands
            ADD COLUMN IF NOT EXISTS Progress INTEGER NOT NULL DEFAULT 0,
            ADD COLUMN IF NOT EXISTS Result TEXT,
            ADD COLUMN IF NOT EXISTS UpdatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW();";

        internal const string CreateTrackedMessagesTable = @"
            CREATE TABLE IF NOT EXISTS TrackedMessages (
                MessageId SERIAL PRIMARY KEY,
                SessionId INTEGER REFERENCES Sessions(SessionId),
                ChatId BIGINT NOT NULL,
                MessageIdPg INTEGER NOT NULL,
                CreatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );";

        internal const string MakeTrackedMessagesSessionNullable = @"
            ALTER TABLE TrackedMessages
            ALTER COLUMN SessionId DROP NOT NULL;";

        internal const string CreateIndexes = @"
            CREATE INDEX IF NOT EXISTS idx_commands_status ON Commands(Status);
            CREATE INDEX IF NOT EXISTS idx_commands_session ON Commands(SessionId);
            CREATE INDEX IF NOT EXISTS idx_commands_status_lease ON Commands(Status, Lease)
                WHERE Status = 'processing';
            CREATE INDEX IF NOT EXISTS idx_sessions_user_created ON Sessions(UserId, CreatedAt DESC);
            DROP INDEX IF EXISTS idx_commands_pending_priority;
            CREATE INDEX IF NOT EXISTS idx_commands_pending_priority ON Commands(Status, Priority ASC, CreatedAt ASC, CommandId ASC)
                WHERE Status = 'pending';
            CREATE INDEX IF NOT EXISTS idx_commands_partition_status ON Commands(Partition, Status);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_commands_unique ON Commands(SessionId, CommandText, FilePath);
            CREATE INDEX IF NOT EXISTS idx_tracked_messages_session ON TrackedMessages(SessionId);
            CREATE INDEX IF NOT EXISTS idx_tracked_messages_chat ON TrackedMessages(ChatId);
            CREATE INDEX IF NOT EXISTS idx_commands_updated_at ON Commands(UpdatedAt DESC);";
    }
}
