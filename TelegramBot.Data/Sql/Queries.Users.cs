namespace TelegramBot.Data;

internal static partial class SqlQueries
{
    internal static class Users
    {
        internal const string GetById =
            "SELECT UserId, Username, Role, Status, CreatedAt, UpdatedAt FROM BotUsers WHERE UserId = @UserId;";

        internal const string Upsert = @"
            INSERT INTO BotUsers (UserId, Username, Role, Status, CreatedAt, UpdatedAt)
            VALUES (@UserId, @Username, @Role, @Status, @CreatedAt, @UpdatedAt)
            ON CONFLICT(UserId) DO UPDATE SET
                Username  = excluded.Username,
                Role      = excluded.Role,
                Status    = excluded.Status,
                UpdatedAt = excluded.UpdatedAt;";
    }
}
