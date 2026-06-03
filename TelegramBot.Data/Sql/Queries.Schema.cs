namespace TelegramBot.Data;

internal static partial class SqlQueries
{
    internal static class Schema
    {
        internal const string CreateBotUsersTable = @"
            CREATE TABLE IF NOT EXISTS BotUsers (
                UserId INTEGER PRIMARY KEY,
                Username TEXT,
                Role INTEGER NOT NULL DEFAULT 0,
                Status INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UpdatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );";

        internal const string CreateSessionsTable = @"
            CREATE TABLE IF NOT EXISTS Sessions (
                SessionId INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId INTEGER NOT NULL,
                Username TEXT,
                PriorityId INTEGER NOT NULL DEFAULT 0,
                Status TEXT NOT NULL DEFAULT 'pending',
                CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                FilesAmount INTEGER,
                UpdatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );";

        internal const string CreateCommandsTable = @"
            CREATE TABLE IF NOT EXISTS Commands (
                CommandId INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId INTEGER NOT NULL,
                CommandText TEXT NOT NULL,
                FilePath TEXT,
                ExecutionOrder INTEGER NOT NULL,
                Status TEXT NOT NULL DEFAULT 'pending',
                CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                GUID TEXT,
                Lease INTEGER DEFAULT 3600,
                FOREIGN KEY (SessionId) REFERENCES Sessions(SessionId)
            );";

        internal const string CheckRoleColumnExists =
            "SELECT COUNT(*) FROM pragma_table_info('BotUsers') WHERE name='Role';";

        internal const string CheckStatusColumnExists =
            "SELECT COUNT(*) FROM pragma_table_info('BotUsers') WHERE name='Status';";

        internal const string AddRoleColumn =
            "ALTER TABLE BotUsers ADD COLUMN Role INTEGER NOT NULL DEFAULT 0;";

        internal const string AddStatusColumn =
            "ALTER TABLE BotUsers ADD COLUMN Status INTEGER NOT NULL DEFAULT 0;";

        internal const string CreateTrackedMessagesTable = @"
            CREATE TABLE IF NOT EXISTS TrackedMessages (
                UserId    INTEGER NOT NULL,
                MessageId INTEGER NOT NULL,
                PRIMARY KEY (UserId, MessageId)
            );";
    }
}
