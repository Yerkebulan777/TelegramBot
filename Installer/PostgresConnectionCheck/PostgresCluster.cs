using System.Diagnostics;
using System.Security.Cryptography;
using Npgsql;

namespace TelegramBot.Installer;

/// <summary>
/// Поднимает PostgreSQL 18 в уже запущенном Docker Desktop и возвращает
/// строку подключения к <c>telegram_bot</c>. Каталог Compose всегда
/// <c>%ProgramData%\TelegramBot\PostgreSQL</c>. Нет Docker — нет базы.
/// </summary>
internal static class PostgresCluster
{
    private const string DatabaseName = "telegram_bot";
    private const string UserName = "postgres";

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
        string password = await EnsurePasswordAsync(cancellationToken);
        string connectionString = AppConnectionString(password);

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

    private static async Task<string> EnsurePasswordAsync(CancellationToken cancellationToken)
    {
        string envPath = Path.Combine(ComposeDirectory, ".env");
        if (File.Exists(envPath))
        {
            foreach (string line in File.ReadLines(envPath))
            {
                const string prefix = "POSTGRES_PASSWORD=";
                string trimmed = line.Trim();
                if (trimmed.StartsWith(prefix, StringComparison.Ordinal) && trimmed.Length > prefix.Length)
                {
                    await Console.Out.WriteLineAsync($"Reusing PostgreSQL credentials from {envPath}.");
                    return trimmed[prefix.Length..];
                }
            }
        }

        string password = "Tb" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + "9x!";
        string contents =
            $"POSTGRES_DB={DatabaseName}{Environment.NewLine}" +
            $"POSTGRES_USER={UserName}{Environment.NewLine}" +
            $"POSTGRES_PASSWORD={password}{Environment.NewLine}";
        await File.WriteAllTextAsync(envPath, contents, cancellationToken);
        await Console.Out.WriteLineAsync($"Wrote PostgreSQL credentials to {envPath}.");
        return password;
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

    private static string AppConnectionString(string password)
        => new NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Database = DatabaseName,
            Username = UserName,
            Password = password,
            Timeout = 30,
            MinPoolSize = 2,
            ConnectionIdleLifetime = 300,
        }.ConnectionString;
}
