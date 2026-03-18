using Dapper;
using Microsoft.Data.Sqlite;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services;

public class SqliteDataService : IDataService
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteDataService> _logger;

    public SqliteDataService(string dbPath, ILogger<SqliteDataService> logger)
    {
        _connectionString = $"Data Source={dbPath}";
        _logger = logger;
    }

    public SqliteDataService(IConfiguration configuration, ILogger<SqliteDataService> logger)
    {
        _connectionString = $"Data Source={configuration.GetConnectionString("Sqlite") ?? "botdata.db"}";
        _logger = logger;
    }


    public async Task InitializeDatabaseAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var createSessionsTable = @"
        CREATE TABLE IF NOT EXISTS Sessions (
            SessionId INTEGER PRIMARY KEY AUTOINCREMENT,
            UserId INTEGER NOT NULL,
            Username TEXT,
            PriorityId INTEGER NOT NULL DEFAULT 0,
            Status TEXT NOT NULL DEFAULT 'pending',
            CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            FilesAmount INTEGER,
            UpdatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
        );
    ";

        var createCommandsTable = @"
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
        );
    ";

        var createWhitelistTable = @"
        CREATE TABLE IF NOT EXISTS Whitelist (
            UserId INTEGER PRIMARY KEY,
            Username TEXT,
            Timestamp TEXT
        );
    ";

        var createCredentialsTable = @"
        CREATE TABLE IF NOT EXISTS Credentials (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            password TEXT NOT NULL
        );
    ";

        await using var cmd1 = new SqliteCommand(createSessionsTable, conn);
        await using var cmd2 = new SqliteCommand(createCommandsTable, conn);
        await using var cmd3 = new SqliteCommand(createWhitelistTable, conn);
        await using var cmd4 = new SqliteCommand(createCredentialsTable, conn);

        await cmd1.ExecuteNonQueryAsync();
        await cmd2.ExecuteNonQueryAsync();
        await cmd3.ExecuteNonQueryAsync();
        await cmd4.ExecuteNonQueryAsync();

        await using var countCmd = new SqliteCommand("SELECT COUNT(*) FROM Credentials;", conn);
        var count = Convert.ToInt64(await countCmd.ExecuteScalarAsync());

        if (count == 0)
        {
            // Intentional design: the default password is hardcoded and known.
            // It is stored as a bcrypt/PBKDF2 hash, not plain-text.
            // The administrator is expected to change it via the database directly after first run.
            string hashedPassword = PasswordHasher.Hash("qwerty123");
            var insertPassword = "INSERT INTO Credentials (password) VALUES (@password);";
            await using var insertCmd = new SqliteCommand(insertPassword, conn);
            insertCmd.Parameters.AddWithValue("@password", hashedPassword);
            await insertCmd.ExecuteNonQueryAsync();
            _logger.LogInformation("Default password (hashed) added to Credentials table.");
        }
        else
        {
            // Migrate any existing plain-text passwords to hashed format
            await MigratePlainTextPasswordsAsync(conn);
        }
    }

    /// <summary>
    /// One-time migration: detects plain-text passwords and re-hashes them.
    /// </summary>
    private async Task MigratePlainTextPasswordsAsync(SqliteConnection conn)
    {
        var selectCmd = new SqliteCommand("SELECT id, password FROM Credentials;", conn);
        await using var reader = await selectCmd.ExecuteReaderAsync();

        var updates = new List<(int Id, string HashedPassword)>();
        while (await reader.ReadAsync())
        {
            int id = reader.GetInt32(0);
            string storedPassword = reader.GetString(1);

            // If it doesn't look like a base64-encoded PBKDF2 hash (48 bytes → 64 chars base64), migrate it
            if (!IsLikelyHash(storedPassword))
            {
                updates.Add((id, PasswordHasher.Hash(storedPassword)));
                _logger.LogInformation("Migrating plain-text password (id={CredentialId}) to hashed format.", id);
            }
        }
        await reader.CloseAsync();

        foreach (var (id, hashedPassword) in updates)
        {
            var updateCmd = new SqliteCommand("UPDATE Credentials SET password = @password WHERE id = @id;", conn);
            updateCmd.Parameters.AddWithValue("@password", hashedPassword);
            updateCmd.Parameters.AddWithValue("@id", id);
            await updateCmd.ExecuteNonQueryAsync();
        }
    }

    private static bool IsLikelyHash(string value)
    {
        // A PBKDF2 hash in our format is base64-encoded 48 bytes → always 64 chars
        if (value.Length < 40) return false;
        try
        {
            byte[] decoded = Convert.FromBase64String(value);
            return decoded.Length == 48; // 16 salt + 32 hash
        }
        catch (FormatException)
        {
            return false;
        }
    }


    // ✅ Update command status (e.g. pending → in_progress → done)
    public async Task UpdateCommandStatusAsync(int commandId, string status)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        const string query = "UPDATE Commands SET Status = @status WHERE CommandId = @id";
        await using var cmd = new SqliteCommand(query, conn);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@id", commandId);
        await cmd.ExecuteNonQueryAsync();
    }

    // ✅ Check if user is in whitelist
    public async Task<bool> IsUserAuthorizedAsync(long userId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        const string query = "SELECT COUNT(*) FROM Whitelist WHERE UserId = @userId";
        await using var cmd = new SqliteCommand(query, conn);
        cmd.Parameters.AddWithValue("@userId", userId);

        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        return count > 0;
    }

    // ✅ Add authorized user to whitelist
    public async Task AddAuthorizedUserAsync(long userId, string username)
    {
        DateTime timestamp = DateTime.UtcNow;
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        const string query = @"
            INSERT OR REPLACE INTO Whitelist (UserId, Username, Timestamp)
            VALUES (@userId, @username, @ts);
        ";

        await using var cmd = new SqliteCommand(query, conn);
        cmd.Parameters.AddWithValue("@userId", userId);
        cmd.Parameters.AddWithValue("@username", username);
        cmd.Parameters.AddWithValue("@ts", timestamp.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }


    // ✅ Get all user commands
    public async Task<List<Command>> GetUserCommandsAsync(long userId)
    {
        var list = new List<Command>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        const string query = @"
            SELECT c.CommandId, s.UserId, c.FilePath, c.CommandText, c.Status, c.CreatedAt
            FROM Commands c
            JOIN Sessions s ON s.SessionId = c.SessionId
            WHERE s.UserId = @userId AND c.Status != 'Deleted';
        ";
        await using var cmd = new SqliteCommand(query, conn);
        cmd.Parameters.AddWithValue("@userId", userId);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new Command
            {
                Id = reader.GetInt32(0),
                UserId = reader.GetInt64(1),
                FileName = reader.GetString(2),
                CommandText = reader.GetString(3),
                Status = reader.GetString(4),
                Timestamp = DateTime.Parse(reader.GetString(5))
            });
        }

        return list;
    }


    public async Task<bool> ValidatePasswordAsync(string password)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        const string query = @"SELECT password FROM Credentials LIMIT 1;";
        await using var cmd = new SqliteCommand(query, conn);

        var storedHash = await cmd.ExecuteScalarAsync() as string;
        if (storedHash == null)
            return false;

        return PasswordHasher.Verify(password, storedHash);
    }

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

        // 1) Insert session + get ID
        var sessionId = await conn.ExecuteScalarAsync<long>(
            @"INSERT INTO Sessions (UserId, Username, PriorityId, FilesAmount)
          VALUES (@UserId, @Username, @PriorityId, @FilesAmount);

          SELECT last_insert_rowid();",
            new { UserId = userId, Username = username, PriorityId = priorityId, FilesAmount = filesAmount },
            tx);

        // 2) Insert commands
        var order = 1;
        foreach (var command in commandText)
        {
            foreach (var file in files)
            {
                await conn.ExecuteAsync(
                    @"INSERT INTO Commands (SessionId, CommandText, FilePath, ExecutionOrder)
              VALUES (@SessionId, @CommandText, @FilePath, @Order)",
                    new
                    {
                        SessionId = sessionId,
                        CommandText = command,
                        FilePath = file,
                        Order = order++
                    },
                    tx);
            }
        }


        await tx.CommitAsync();
        return sessionId;
    }

    public async Task<List<SessionsList>> GetSessionsListAsync(long userId)
    {
        var list = new List<SessionsList>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        // Limit to 20 most recent sessions to avoid keyboard overflow (Telegram limit ~100 buttons)
        const string query = @"
            SELECT SessionId, CreatedAt FROM Sessions
            WHERE UserId = @userId AND Status != 'Deleted'
            ORDER BY CreatedAt DESC
            LIMIT 20;";
        await using var cmd = new SqliteCommand(query, conn);
        cmd.Parameters.AddWithValue("@userId", userId);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // Safe date parsing: SQLite stores dates as TEXT; format may vary across environments
            var rawDate = reader.GetString(1);
            var parsedDate = DateTime.TryParse(rawDate, out var dt) ? dt : DateTime.MinValue;

            list.Add(new SessionsList
            {
                SessionId = reader.GetInt32(0),
                Date = parsedDate,
            });
        }
        return list;
    }

    public async Task<SessionStatus> GetSessionsStatusAsync(int sessionId, long userId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        const string query = @"
            SELECT
                s.Status,
                COUNT(CASE WHEN c.Status != 'Deleted' THEN 1 END) AS TotalFiles,
                COUNT(CASE WHEN c.Status = 'Done'    THEN 1 END) AS DoneFiles
            FROM Sessions s
            LEFT JOIN Commands c ON c.SessionId = s.SessionId
            WHERE s.SessionId = @sessionId
              AND s.UserId = @userId
            GROUP BY s.Status;
        ";

        await using var cmd = new SqliteCommand(query, conn);
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        cmd.Parameters.AddWithValue("@userId", userId);
        await using var reader = await cmd.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
            throw new KeyNotFoundException($"Session {sessionId} not found");

        return new SessionStatus
        {
            Status = reader.IsDBNull(0) ? "Invalid" : reader.GetString(0),
            TotalFiles = reader.GetInt32(1),
            DoneFiles = reader.GetInt32(2),
        };
    }

    public async Task<List<SessionCommands>> GetSessionsCommandsAsync(int sessionId, long userId)
    {
        var list = new List<SessionCommands>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        const string query = @"
            SELECT c.ExecutionOrder, c.CommandText, c.FilePath, c.Status, c.CreatedAt, c.CommandId
            FROM Commands c
            JOIN Sessions s ON s.SessionId = c.SessionId
            WHERE c.SessionId = @sessionId
              AND s.UserId = @userId
              AND c.Status != 'Deleted';";
        await using var cmd = new SqliteCommand(query, conn);
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        cmd.Parameters.AddWithValue("@userId", userId);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new SessionCommands
            {
                ExecOrder = reader.GetInt32(0),
                Command = reader.GetString(1),
                FileName = reader.GetString(2),
                Status = reader.GetString(3),
                Date = reader.GetDateTime(4),
                CommandId = reader.GetInt32(5)
            });
        }

        return list;
    }

    public async Task<bool> DeleteSessionAsync(int sessionId, long userId)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            await using var tx = await conn.BeginTransactionAsync();
            try
            {
                const string deleteSessionQuery = @"
                UPDATE Sessions
                SET Status = 'Deleted'
                WHERE SessionId = @sessionId
                  AND UserId = @userId;
            ";

                await using var deleteSessionCmd = new SqliteCommand(deleteSessionQuery, conn, (SqliteTransaction)tx);
                deleteSessionCmd.Parameters.AddWithValue("@sessionId", sessionId);
                deleteSessionCmd.Parameters.AddWithValue("@userId", userId);
                var affectedSessions = await deleteSessionCmd.ExecuteNonQueryAsync();
                if (affectedSessions == 0)
                {
                    await tx.RollbackAsync();
                    _logger.LogWarning("Attempt to delete foreign or missing session {SessionId} by user {UserId}", sessionId, userId);
                    return false;
                }

                const string deleteCommandsQuery = @"
                UPDATE Commands
                SET Status = 'Deleted'
                WHERE SessionId = @sessionId;
            ";

                await using var deleteCommandsCmd = new SqliteCommand(deleteCommandsQuery, conn, (SqliteTransaction)tx);
                deleteCommandsCmd.Parameters.AddWithValue("@sessionId", sessionId);
                await deleteCommandsCmd.ExecuteNonQueryAsync();

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



    public async Task<bool> DeleteCommandAsync(int commandId, long userId)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();


            const string query = @"
            UPDATE Commands
            SET Status = 'Deleted'
            WHERE CommandId = @commandId
              AND SessionId IN (
                    SELECT SessionId
                    FROM Sessions
                    WHERE UserId = @userId
                );
        ";
            await using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@commandId", commandId);
            cmd.Parameters.AddWithValue("@userId", userId);
            var affectedRows = await cmd.ExecuteNonQueryAsync();
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


    public async Task<bool> CheckCommandsStatusAsync(int sessionId, long userId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = new SqliteCommand(
            @"SELECT COUNT(*)
              FROM Commands c
              JOIN Sessions s ON s.SessionId = c.SessionId
              WHERE c.SessionId = @sessionId
                AND s.UserId = @userId
                AND c.Status != 'Deleted';", conn);
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        cmd.Parameters.AddWithValue("@userId", userId);

        var result = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        return result > 0;
    }

    public async Task<int?> GetSessionIdByCommandAsync(int commandId, long userId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        const string query = @"
            SELECT c.SessionId
            FROM Commands c
            JOIN Sessions s ON s.SessionId = c.SessionId
            WHERE c.CommandId = @commandId
              AND s.UserId = @userId
              AND c.Status != 'Deleted'
              AND s.Status != 'Deleted'
            LIMIT 1;";

        await using var cmd = new SqliteCommand(query, conn);
        cmd.Parameters.AddWithValue("@commandId", commandId);
        cmd.Parameters.AddWithValue("@userId", userId);

        var value = await cmd.ExecuteScalarAsync();
        if (value is null || value == DBNull.Value)
            return null;

        return Convert.ToInt32(value);
    }
}
