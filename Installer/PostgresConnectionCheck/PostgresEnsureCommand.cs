namespace TelegramBot.Installer;

internal sealed record PostgresEnsureArguments(string? ServerDirectory, string? WorkerDirectory);

/// <summary>
/// Готовит строку подключения для выбранных компонентов. Контейнер поднимает
/// <see cref="PostgresCluster"/> только если ни один целевой каталог (и при
/// установке только Worker — соседний Server) её не дал.
/// </summary>
internal static class PostgresEnsureCommand
{
    internal static async Task<int> RunAsync(PostgresEnsureArguments arguments)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(45));
        CancellationToken cancellationToken = timeout.Token;

        HashSet<string> targets = new(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(arguments.ServerDirectory))
        {
            targets.Add(Path.GetFullPath(arguments.ServerDirectory));
        }

        if (!string.IsNullOrWhiteSpace(arguments.WorkerDirectory))
        {
            targets.Add(Path.GetFullPath(arguments.WorkerDirectory));
        }

        if (targets.Count == 0)
        {
            await Console.Error.WriteLineAsync("ensure requires --server and/or --worker.");
            return 2;
        }

        string? connectionString = null;
        List<string> pending = [];
        foreach (string directory in targets)
        {
            string? localConnection = ApplicationSettings.TryGetLocalConnectionString(directory);
            if (localConnection is not null)
            {
                try
                {
                    await ApplicationSettings.PingAsync(localConnection, cancellationToken);
                    connectionString ??= localConnection;
                    continue;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await Console.Error.WriteLineAsync(
                        $"Configured PostgreSQL settings for {directory} are not reachable: {ex.Message}");
                    return 1;
                }
            }

            string? existing = await ApplicationSettings.TryGetWorkingConnectionAsync(directory, cancellationToken);
            if (existing is null)
            {
                pending.Add(directory);
            }
            else
            {
                connectionString ??= existing;
            }
        }

        if (pending.Count == 0)
        {
            await Console.Out.WriteLineAsync("PostgreSQL is already reachable for the selected components.");
            return 0;
        }

        if (connectionString is null
            && string.IsNullOrWhiteSpace(arguments.ServerDirectory)
            && !string.IsNullOrWhiteSpace(arguments.WorkerDirectory))
        {
            connectionString = await TrySiblingServerConnectionAsync(arguments.WorkerDirectory, cancellationToken);
        }

        if (connectionString is null)
        {
            if (string.IsNullOrWhiteSpace(arguments.ServerDirectory))
            {
                await Console.Error.WriteLineAsync(
                    "PostgreSQL is not reachable for Worker. Install Server on this computer " +
                    "or set ConnectionStrings:Postgres in Worker appsettings.Local.json to the Server database.");
                return 1;
            }

            connectionString = await PostgresCluster.EnsureAppDatabaseAsync(cancellationToken);
        }

        await ApplicationSettings.PingAsync(connectionString, cancellationToken);

        foreach (string directory in pending)
        {
            await ApplicationSettings.WriteConnectionStringAsync(directory, connectionString, cancellationToken);
        }

        await Console.Out.WriteLineAsync("PostgreSQL is ready for TelegramBot.");
        return 0;
    }

    private static async Task<string?> TrySiblingServerConnectionAsync(
        string workerDirectory,
        CancellationToken cancellationToken)
    {
        string? parent = Path.GetDirectoryName(Path.GetFullPath(workerDirectory));
        if (string.IsNullOrWhiteSpace(parent))
        {
            return null;
        }

        string siblingServer = Path.Combine(parent, "Server");
        return Directory.Exists(siblingServer)
            ? await ApplicationSettings.TryGetWorkingConnectionAsync(siblingServer, cancellationToken)
            : null;
    }
}
