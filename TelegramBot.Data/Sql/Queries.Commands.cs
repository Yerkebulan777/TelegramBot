namespace TelegramBot.Data;

internal static partial class SqlQueries
{
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
