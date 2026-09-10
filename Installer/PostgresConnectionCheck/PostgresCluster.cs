using System.Diagnostics;
using System.Security.Cryptography;
using Npgsql;

namespace TelegramBot.Installer;

/// <summary>
/// Поднимает PostgreSQL 18 в уже запущенном Docker Desktop и возвращает
/// строку подключения к PostgreSQL из <c>%ProgramData%\TelegramBot\PostgreSQL\.env</c>
/// (или к стандартным <c>telegram_bot/postgres</c>, если файл ещё не создан).
/// Каталог Compose всегда <c>%ProgramData%\TelegramBot\PostgreSQL</c>. Нет Docker — нет базы.
/// </summary>
internal static class PostgresCluster
{
    private const string DefaultDatabaseName = "telegram_bot";
    private const string DefaultUserName = "postgres";

    private static readonly string ComposeDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "TelegramBot",
        "PostgreSQL");

    internal static async Task<string> EnsureAppDatabaseAsync(CancellationToken cancellationToken)
    {
        string composeFile = Path.Combine(ComposeDirectory, "docker-compose.yml");
        if (!File.Exists(composeFile))
        {
            throw new InvalidOperationException(
                $"Не найден docker-compose.yml в {ComposeDirectory}. Переустановите TelegramBot.");
        }

        string dockerExe = await RequireRunningDockerAsync(cancellationToken);
        PostgresSettings settings = await EnsureSettingsAsync(cancellationToken);
        string connectionString = AppConnectionString(settings.DatabaseName, settings.UserName, settings.Password);

        await Console.Out.WriteLineAsync("Starting PostgreSQL 18 in Docker Desktop.");
        await RunDockerAsync(
            dockerExe,
            ["compose", "--project-directory", ComposeDirectory, "up", "-d", "--wait", "--wait-timeout", "120"],
            cancellationToken);
        await ApplicationSettings.PingAsync(connectionString, cancellationToken);
        return connectionString;
    }

    private static async Task<string> RequireRunningDockerAsync(CancellationToken cancellationToken)
    {
        string dockerExe = FindDockerCli();
        try
        {
            await RunDockerAsync(dockerExe, ["info"], cancellationToken);
            return dockerExe;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "Docker Desktop для Windows должен быть установлен и запущен. " +
                "Установщик не ставит PostgreSQL отдельно: запустите Docker Desktop и повторите Setup." +
                (string.IsNullOrWhiteSpace(ex.Message) ? string.Empty : Environment.NewLine + ex.Message),
                ex);
        }
    }

    private static string FindDockerCli()
    {
        string wellKnown = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Docker",
            "Docker",
            "resources",
            "bin",
            "docker.exe");
        if (File.Exists(wellKnown))
        {
            return wellKnown;
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(directory.Trim('"'), "docker.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new InvalidOperationException(
            "Docker Desktop для Windows должен быть установлен и запущен. " +
            "Не найден docker.exe. Установите Docker Desktop, запустите его и повторите Setup.");
    }

    private static async Task<PostgresSettings> EnsureSettingsAsync(CancellationToken cancellationToken)
    {
        string envPath = Path.Combine(ComposeDirectory, ".env");
        if (File.Exists(envPath))
        {
            string? databaseName = null;
            string? userName = null;
            string? password = null;
            foreach (string line in File.ReadLines(envPath))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("POSTGRES_DB=", StringComparison.Ordinal) && trimmed.Length > "POSTGRES_DB=".Length)
                {
                    databaseName = trimmed["POSTGRES_DB=".Length..];
                }
                else if (trimmed.StartsWith("POSTGRES_USER=", StringComparison.Ordinal) && trimmed.Length > "POSTGRES_USER=".Length)
                {
                    userName = trimmed["POSTGRES_USER=".Length..];
                }
                else if (trimmed.StartsWith("POSTGRES_PASSWORD=", StringComparison.Ordinal) && trimmed.Length > "POSTGRES_PASSWORD=".Length)
                {
                    await Console.Out.WriteLineAsync($"Reusing PostgreSQL credentials from {envPath}.");
                    password = trimmed["POSTGRES_PASSWORD=".Length..];
                }
            }

            if (!string.IsNullOrWhiteSpace(password))
            {
                return new PostgresSettings(
                    string.IsNullOrWhiteSpace(databaseName) ? DefaultDatabaseName : databaseName,
                    string.IsNullOrWhiteSpace(userName) ? DefaultUserName : userName,
                    password);
            }
        }

        string generatedPassword = "Tb" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + "9x!";
        string contents =
            $"POSTGRES_DB={DefaultDatabaseName}{Environment.NewLine}" +
            $"POSTGRES_USER={DefaultUserName}{Environment.NewLine}" +
            $"POSTGRES_PASSWORD={generatedPassword}{Environment.NewLine}";
        await File.WriteAllTextAsync(envPath, contents, cancellationToken);
        await Console.Out.WriteLineAsync($"Wrote PostgreSQL credentials to {envPath}.");
        return new PostgresSettings(DefaultDatabaseName, DefaultUserName, generatedPassword);
    }

    private static async Task RunDockerAsync(
        string dockerExe,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = dockerExe,
            WorkingDirectory = ComposeDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start docker.exe.");

        string standardOutput = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        string standardError = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode == 0)
        {
            return;
        }

        string output = string.Join(
            Environment.NewLine,
            new[] { standardOutput, standardError }.Where(static text => !string.IsNullOrWhiteSpace(text)));
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(output)
                ? $"docker exited with code {process.ExitCode}."
                : $"docker exited with code {process.ExitCode}:{Environment.NewLine}{output}");
    }

    private static string AppConnectionString(string databaseName, string userName, string password)
        => new NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Database = databaseName,
            Username = userName,
            Password = password,
            Timeout = 30,
            MinPoolSize = 2,
            ConnectionIdleLifetime = 300,
        }.ConnectionString;

    private sealed record PostgresSettings(string DatabaseName, string UserName, string Password);
}
