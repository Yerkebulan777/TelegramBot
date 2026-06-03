namespace TelegramBot.Data;

internal static class SqlQueries
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

        // Check whether a column exists in pragma_table_info — used for migrations.
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

    internal static class TrackedMessages
    {
        internal const string Insert =
            "INSERT OR IGNORE INTO TrackedMessages (UserId, MessageId) VALUES (@UserId, @MessageId);";

        internal const string GetAll =
            "SELECT UserId, MessageId FROM TrackedMessages;";

        internal const string DeleteByUser =
            "DELETE FROM TrackedMessages WHERE UserId = @UserId;";
    }

    internal static class Users
    {
        internal const string GetById =
            "SELECT UserId, Username, Role, Status, CreatedAt, UpdatedAt FROM BotUsers WHERE UserId = @UserId;";

        internal const string GetStatusById =
            "SELECT Status FROM BotUsers WHERE UserId = @UserId;";

        internal const string Upsert = @"
            INSERT INTO BotUsers (UserId, Username, Role, Status, CreatedAt, UpdatedAt)
            VALUES (@UserId, @Username, @Role, @Status, @CreatedAt, @UpdatedAt)
            ON CONFLICT(UserId) DO UPDATE SET
                Username  = excluded.Username,
                Role      = excluded.Role,
                Status    = excluded.Status,
                UpdatedAt = excluded.UpdatedAt;";

        internal const string InsertIgnoreConflict = @"
            INSERT INTO BotUsers (UserId, Username, Role, Status, CreatedAt, UpdatedAt)
            VALUES (@UserId, @Username, @Role, @Status, @CreatedAt, @UpdatedAt)
            ON CONFLICT(UserId) DO NOTHING;";

        internal const string UpsertAdmin = @"
            INSERT INTO BotUsers (UserId, Username, Role, Status, CreatedAt, UpdatedAt)
            VALUES (@UserId, @Username, @Role, @Status, @CreatedAt, @UpdatedAt)
            ON CONFLICT(UserId) DO UPDATE SET
                Role      = excluded.Role,
                Status    = excluded.Status,
                UpdatedAt = excluded.UpdatedAt;";

        internal const string UpdateStatus = @"
            UPDATE BotUsers SET Status = @Status, UpdatedAt = @UpdatedAt
            WHERE UserId = @UserId;";
    }

    internal static class Sessions
    {
        internal const string Insert = @"
            INSERT INTO Sessions (UserId, Username, FilesAmount)
            VALUES (@UserId, @Username, @FilesAmount);
            SELECT last_insert_rowid();";

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
    }
}
