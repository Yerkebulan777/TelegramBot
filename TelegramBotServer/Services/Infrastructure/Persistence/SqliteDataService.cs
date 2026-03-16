using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
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
            var insertPassword = "INSERT INTO Credentials (password) VALUES ('qwerty123');";
            await using var insertCmd = new SqliteCommand(insertPassword, conn);
            await insertCmd.ExecuteNonQueryAsync();
            _logger.LogInformation("Default password added to Credentials table.");
        }
    }




    // ?? Add new command to queue
    public async Task AddCommandAsync(long userId, List<string> fileList, List<string> commandList)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        const string query = @"
            INSERT INTO Commands (UserId, FileName, CommandText, Status, Timestamp)
            VALUES (@userId, @fileName, @commandText, 'queued', @ts);
        ";



        foreach (var commandText in commandList)
        {
            foreach (var file in fileList)
            {
                using var cmd = new SqliteCommand(query, conn);
                cmd.Parameters.AddWithValue("@userId", userId);
                cmd.Parameters.AddWithValue("@fileName", file);
                cmd.Parameters.AddWithValue("@commandText", commandText);
                cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("o"));
                await cmd.ExecuteNonQueryAsync();
            }
        }



    }

    // ?? Get first queued command
    public async Task<Command?> GetNextCommandAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        const string query = @"
            SELECT * FROM Commands
            WHERE Status = 'queued'
            ORDER BY Timestamp ASC
            LIMIT 1;
        ";

        await using var cmd = new SqliteCommand(query, conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        if (await reader.ReadAsync())
        {
            return new Command
            {
                Id = reader.GetInt32(0),
                UserId = reader.GetInt64(1),
                FileName = reader.GetString(2),
                CommandText = reader.GetString(3),
                Status = reader.GetString(4),
                Timestamp = DateTime.Parse(reader.GetString(5))
            };
        }

        return null;
    }

    // ?? Update command status (e.g. queued > in_progress > done)
    public async Task UpdateCommandStatusAsync(int commandId, string status)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        const string query = "UPDATE Commands SET Status = @status WHERE Id = @id";
        await using var cmd = new SqliteCommand(query, conn);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@id", commandId);
        await cmd.ExecuteNonQueryAsync();
    }

    // ?? Check if user is in whitelist
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

    // ?? Add authorized user to whitelist
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











    // ?? Get all user commands
    public async Task<List<Command>> GetUserCommandsAsync(long userId)
    {
        var list = new List<Command>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        const string query = "SELECT * FROM Commands WHERE UserId = @userId and Status !='Deleted';";
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


        const string query = @"
            SELECT COUNT(*) 
            FROM Credentials
            WHERE password = @password;
        ";




        await using var cmd = new SqliteCommand(query, conn);
        cmd.Parameters.AddWithValue("@password", password);


        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());

        return count > 0;
    }



    public async Task<bool> RemoveCommandFromQueue(int id)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();


            const string query = @"
            UPDATE Commands
            SET Status = 'Deleted'
            WHERE Id = @id;
        ";
            await using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@id", id);
            await cmd.ExecuteNonQueryAsync();
            return true;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to remove command {CommandId} from queue", id);
            return false;
        }
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

        const string query = "SELECT SessionId, CreatedAt FROM Sessions WHERE UserId = @userId and Status !='Deleted';";
        await using var cmd = new SqliteCommand(query, conn);
        cmd.Parameters.AddWithValue("@userId", userId);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new SessionsList
            {
                SessionId = reader.GetInt32(0),
                Date = reader.GetDateTime(1),
            });
        }
        return list;
    }

    public async Task<SessionStatus> GetSessionsStatusAsync(int sessionId)
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
            GROUP BY s.Status;
        ";

        await using var cmd = new SqliteCommand(query, conn);
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        await using var reader = await cmd.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
            throw new Exception($"Session {sessionId} not found");

        return new SessionStatus
        {
            Status     = reader.IsDBNull(0) ? "Invalid" : reader.GetString(0),
            TotalFiles = reader.GetInt32(1),
            DoneFiles  = reader.GetInt32(2),
        };
    }

    public async Task<List<SessionCommands>> GetSessionsCommandsAsync(int sessionId)
    {
        var list = new List<SessionCommands>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        const string query = "select ExecutionOrder, CommandText, FilePath, Status, CreatedAt, CommandId from Commands where SessionId = @sessionId AND Status != 'Deleted';";
        await using var cmd = new SqliteCommand(query, conn);
        cmd.Parameters.AddWithValue("@sessionId", sessionId);

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

    public async Task<bool> DeleteSessionAsync(int sessionId)
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
                WHERE SessionId = @sessionId;
            ";

                await using var deleteSessionCmd = new SqliteCommand(deleteSessionQuery, conn, (SqliteTransaction)tx);
                deleteSessionCmd.Parameters.AddWithValue("@sessionId", sessionId);
                await deleteSessionCmd.ExecuteNonQueryAsync();

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



    public async Task<bool> DeleteCommandAsync(int commandId)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();


            const string query = @"
            UPDATE Commands
            SET Status = 'Deleted'
            WHERE CommandId = @commandId;
        ";
            await using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@commandId", commandId);
            await cmd.ExecuteNonQueryAsync();
            return true;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to delete command {CommandId}", commandId);
            return false;
        }
    }


    public async Task<bool> CheckCommandsStatusAsync(int sessionId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = new SqliteCommand(
            "SELECT COUNT(*) FROM Commands WHERE SessionId = @sessionId AND Status != 'Deleted';", conn);
        cmd.Parameters.AddWithValue("@sessionId", sessionId);

        var result = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        return result > 0;
    }


}