#nullable enable

using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;

namespace TelegramBot.Data;

/// <summary>
/// SQLite persistence service using Dapper for all data access.
/// </summary>
public class SqliteDataService(IConfiguration configuration, ILogger<SqliteDataService> logger) : IDataService
{
    private readonly string _connectionString = configuration.GetConnectionString("Sqlite") ?? "Data Source=botdata.db";
    private readonly ILogger<SqliteDataService> _logger = logger;

    /// <summary>
    /// Инициализирует базу данных (создает таблицы, если не существуют).
    /// </summary>
    public async Task InitializeDatabaseAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        const string createUsersTable = @"
            CREATE TABLE IF NOT EXISTS BotUsers (
                UserId INTEGER PRIMARY KEY,
                Username TEXT,
                Role INTEGER NOT NULL DEFAULT 0,
                Status INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UpdatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );";

        const string createSessionsTable = @"
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

        const string createCommandsTable = @"
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

        // Add migration for BotUsers table: add Role column if not exists (for backward compatibility)
        const string addRoleColumn = @"
            SELECT COUNT(*) FROM pragma_table_info('BotUsers') WHERE name='Role';";
        
        var roleColumnExists = await conn.ExecuteScalarAsync<int>(addRoleColumn);
        if (roleColumnExists == 0)
        {
            await conn.ExecuteAsync("ALTER TABLE BotUsers ADD COLUMN Role INTEGER NOT NULL DEFAULT 0;");
        }

        // Add migration for BotUsers table: add Status column if not exists
        const string addStatusColumn = @"
            SELECT COUNT(*) FROM pragma_table_info('BotUsers') WHERE name='Status';";
        
        var statusColumnExists = await conn.ExecuteScalarAsync<int>(addStatusColumn);
        if (statusColumnExists == 0)
        {
            await conn.ExecuteAsync("ALTER TABLE BotUsers ADD COLUMN Status INTEGER NOT NULL DEFAULT 0;");
        }

        await conn.ExecuteAsync(createUsersTable);
        await conn.ExecuteAsync(createSessionsTable);
        await conn.ExecuteAsync(createCommandsTable);
    }

    public async Task<BotUser?> GetUserAsync(long userId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        return await conn.QuerySingleOrDefaultAsync<BotUser>(
            "SELECT UserId, Username, Role, Status, CreatedAt, UpdatedAt FROM BotUsers WHERE UserId = @UserId;",
            new { UserId = userId });
    }

    public async Task UpsertUserAsync(BotUser user)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var now = DateTime.UtcNow;
        await conn.ExecuteAsync(@"
            INSERT INTO BotUsers (UserId, Username, Role, Status, CreatedAt, UpdatedAt)
            VALUES (@UserId, @Username, @Role, @Status, @CreatedAt, @UpdatedAt)
            ON CONFLICT(UserId) DO UPDATE SET
                Username  = excluded.Username,
                Role      = excluded.Role,
                Status    = excluded.Status,
                UpdatedAt = excluded.UpdatedAt;",
            new
            {
                user.UserId,
                user.Username,
                Role = (int)user.Role,
                Status = (int)user.Status,
                CreatedAt = user.CreatedAt == default ? now : user.CreatedAt,
                UpdatedAt = user.UpdatedAt == default ? now : user.UpdatedAt
            });
    }

    /// <summary>
    /// Создает новую сессию с набором команд и файлов.
    /// </summary>
    public async Task<long> CreateSessionWithCommandsAsync(
        IEnumerable<string> commandText,
        IEnumerable<string> files,
        long userId,
        string username,
        int priorityId,
        int filesAmount)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var tx = conn.BeginTransaction();

        var sessionId = await conn.ExecuteScalarAsync<long>(
            @"INSERT INTO Sessions (UserId, Username, PriorityId, FilesAmount)
              VALUES (@UserId, @Username, @PriorityId, @FilesAmount);
              SELECT last_insert_rowid();",
            new { UserId = userId, Username = username, PriorityId = priorityId, FilesAmount = filesAmount },
            tx);

        var order = 1;
        foreach (var command in commandText)
        {
            foreach (var file in files)
            {
                await conn.ExecuteAsync(
                    @"INSERT INTO Commands (SessionId, CommandText, FilePath, ExecutionOrder)
                      VALUES (@SessionId, @CommandText, @FilePath, @Order)",
                    new { SessionId = sessionId, CommandText = command, FilePath = file, Order = order++ },
                    tx);
            }
        }

        await tx.CommitAsync();
        return sessionId;
    }

    /// <summary>
    /// Возвращает список сессий пользователя (до 20 последних).
    /// </summary>
    public async Task<List<SessionsList>> GetSessionsListAsync(long userId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        const string query = @"
            SELECT SessionId, CreatedAt AS Date
            FROM Sessions
            WHERE UserId = @UserId AND Status != 'Deleted'
            ORDER BY CreatedAt DESC
            LIMIT 20;";

        var result = await conn.QueryAsync<SessionsList>(query, new { UserId = userId });
        return result.ToList();
    }

    /// <summary>
    /// Возвращает статус сессии (количество файлов, выполнено, процент).
    /// </summary>
    public async Task<SessionStatus> GetSessionsStatusAsync(int sessionId, long userId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        const string query = @"
            SELECT
                s.Status,
                COUNT(CASE WHEN c.Status != 'Deleted' THEN 1 END) AS TotalFiles,
                COUNT(CASE WHEN c.Status = 'Done'     THEN 1 END) AS DoneFiles
            FROM Sessions s
            LEFT JOIN Commands c ON c.SessionId = s.SessionId
            WHERE s.SessionId = @SessionId
              AND s.UserId = @UserId
            GROUP BY s.Status;";

        var result = await conn.QuerySingleOrDefaultAsync<SessionStatus>(
            query, new { SessionId = sessionId, UserId = userId });

        return result ?? throw new KeyNotFoundException($"Session {sessionId} not found");
    }

    /// <summary>
    /// Возвращает список команд внутри указанной сессии.
    /// </summary>
    public async Task<List<SessionCommands>> GetSessionsCommandsAsync(int sessionId, long userId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        const string query = @"
            SELECT c.ExecutionOrder AS ExecOrder, c.CommandText AS Command,
                   c.FilePath AS FileName, c.Status, c.CreatedAt AS Date, c.CommandId
            FROM Commands c
            JOIN Sessions s ON s.SessionId = c.SessionId
            WHERE c.SessionId = @SessionId
              AND s.UserId = @UserId
              AND c.Status != 'Deleted';";

        var result = await conn.QueryAsync<SessionCommands>(
            query, new { SessionId = sessionId, UserId = userId });
        return result.ToList();
    }

    /// <summary>
    /// Удаляет сессию и все её команды (мягкое удаление).
    /// </summary>
    public async Task<bool> DeleteSessionAsync(int sessionId, long userId)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            await using var tx = await conn.BeginTransactionAsync();
            try
            {
                var affectedSessions = await conn.ExecuteAsync(
                    @"UPDATE Sessions SET Status = 'Deleted'
                      WHERE SessionId = @SessionId AND UserId = @UserId;",
                    new { SessionId = sessionId, UserId = userId }, (SqliteTransaction)tx);

                if (affectedSessions == 0)
                {
                    await tx.RollbackAsync();
                    _logger.LogWarning("Attempt to delete foreign or missing session {SessionId} by user {UserId}", sessionId, userId);
                    return false;
                }

                await conn.ExecuteAsync(
                    @"UPDATE Commands SET Status = 'Deleted' WHERE SessionId = @SessionId;",
                    new { SessionId = sessionId }, (SqliteTransaction)tx);

                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }

            return true;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to delete session {SessionId}", sessionId);
            return false;
        }
    }

    /// <summary>
    /// Удаляет одну команду (мягкое удаление).
    /// </summary>
    public async Task<bool> DeleteCommandAsync(int commandId, long userId)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var affectedRows = await conn.ExecuteAsync(
                @"UPDATE Commands SET Status = 'Deleted'
                  WHERE CommandId = @CommandId
                    AND SessionId IN (SELECT SessionId FROM Sessions WHERE UserId = @UserId);",
                new { CommandId = commandId, UserId = userId });

            if (affectedRows == 0)
            {
                _logger.LogWarning("Attempt to delete foreign or missing command {CommandId} by user {UserId}", commandId, userId);
                return false;
            }

            return true;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to delete command {CommandId}", commandId);
            return false;
        }
    }

    /// <summary>
    /// Проверяет, есть ли команды в указанной сессии.
    /// </summary>
    public async Task<bool> CheckCommandsStatusAsync(int sessionId, long userId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var count = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*)
              FROM Commands c
              JOIN Sessions s ON s.SessionId = c.SessionId
              WHERE c.SessionId = @SessionId
                AND s.UserId = @UserId
                AND c.Status != 'Deleted';",
            new { SessionId = sessionId, UserId = userId });

        return count > 0;
    }

    /// <summary>
    /// Возвращает ID сессии по ID команды.
    /// </summary>
    public async Task<int?> GetSessionIdByCommandAsync(int commandId, long userId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        return await conn.QuerySingleOrDefaultAsync<int?>(
            @"SELECT c.SessionId
              FROM Commands c
              JOIN Sessions s ON s.SessionId = c.SessionId
              WHERE c.CommandId = @CommandId
                AND s.UserId = @UserId
                AND c.Status != 'Deleted'
                AND s.Status != 'Deleted'
              LIMIT 1;",
            new { CommandId = commandId, UserId = userId });
    }

    public async Task CreateAccessRequestAsync(long userId, string? username, string? firstName, string? lastName)
    {
        var now = DateTime.UtcNow;
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await conn.ExecuteAsync(@"
            INSERT INTO BotUsers (UserId, Username, Role, Status, CreatedAt, UpdatedAt)
            VALUES (@UserId, @Username, @Role, @Status, @CreatedAt, @UpdatedAt)
            ON CONFLICT(UserId) DO NOTHING;",
            new
            {
                UserId = userId,
                Username = username,
                Role = (int)UserRole.User,
                Status = (int)UserAccessStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now
            });
    }

    public Task<BotUser?> GetBotUserAsync(long userId) => GetUserAsync(userId);

    public async Task<bool> IsUserApprovedAsync(long userId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var status = await conn.QuerySingleOrDefaultAsync<int?>(
            "SELECT Status FROM BotUsers WHERE UserId = @UserId;",
            new { UserId = userId });

        return status == (int)UserAccessStatus.Approved;
    }

    public async Task<bool> ApproveUserAsync(long userId, long approvedBy)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var rows = await conn.ExecuteAsync(@"
            UPDATE BotUsers SET Status = @Status, UpdatedAt = @UpdatedAt
            WHERE UserId = @UserId;",
            new
            {
                UserId = userId,
                Status = (int)UserAccessStatus.Approved,
                UpdatedAt = DateTime.UtcNow
            });

        return rows > 0;
    }

    public async Task EnsureAdminUserAsync(long userId, string? username)
    {
        var now = DateTime.UtcNow;
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await conn.ExecuteAsync(@"
            INSERT INTO BotUsers (UserId, Username, Role, Status, CreatedAt, UpdatedAt)
            VALUES (@UserId, @Username, @Role, @Status, @CreatedAt, @UpdatedAt)
            ON CONFLICT(UserId) DO UPDATE SET
                Role      = excluded.Role,
                Status    = excluded.Status,
                UpdatedAt = excluded.UpdatedAt;",
            new
            {
                UserId = userId,
                Username = username,
                Role = (int)UserRole.Admin,
                Status = (int)UserAccessStatus.Approved,
                CreatedAt = now,
                UpdatedAt = now
            });
    }
}
