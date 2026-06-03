namespace TelegramBot.Data;

internal static partial class SqlQueries
{
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
}
