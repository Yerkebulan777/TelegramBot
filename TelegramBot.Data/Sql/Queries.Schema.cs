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
                CreatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                FilesAmount INTEGER,
                UpdatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );";

        internal const string CreateCommandsTable = @"
            CREATE TABLE IF NOT EXISTS Commands (
                CommandId SERIAL PRIMARY KEY,
                SessionId INTEGER NOT NULL REFERENCES Sessions(SessionId),
                CommandText TEXT NOT NULL,
                FilePath TEXT,
                ExecutionOrder INTEGER NOT NULL,
                Status TEXT NOT NULL DEFAULT 'pending',
                CreatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                GUID TEXT,
                Lease INTEGER DEFAULT 3600
            );";

        internal const string CreateTrackedMessagesTable = @"
            CREATE TABLE IF NOT EXISTS TrackedMessages (
                UserId    BIGINT NOT NULL,
                MessageId INTEGER NOT NULL,
                PRIMARY KEY (UserId, MessageId)
            );";

        internal const string CreateIndexes = @"
            CREATE INDEX IF NOT EXISTS idx_commands_status ON Commands(Status);
            CREATE INDEX IF NOT EXISTS idx_commands_session ON Commands(SessionId);
            CREATE INDEX IF NOT EXISTS idx_commands_status_lease ON Commands(Status, Lease)
                WHERE Status = 'processing';
            CREATE INDEX IF NOT EXISTS idx_sessions_user_created ON Sessions(UserId, CreatedAt DESC);";
    }
}
