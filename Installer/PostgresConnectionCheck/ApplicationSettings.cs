using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace TelegramBot.Installer;

internal static class ApplicationSettings
{
    private static readonly JsonSerializerOptions LocalJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static string ResolveConnectionString(string applicationDirectory)
    {
        string environmentName =
            Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "Production";
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(applicationDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        return configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Postgres is not configured in the application settings.");
    }

    internal static Task PingDirectoryAsync(string applicationDirectory, CancellationToken cancellationToken)
        => PingAsync(ResolveConnectionString(applicationDirectory), cancellationToken);

    internal static async Task<string?> TryGetWorkingConnectionAsync(
        string applicationDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            string connectionString = ResolveConnectionString(applicationDirectory);
            await PingAsync(connectionString, cancellationToken);
            return connectionString;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Console.Out.WriteLineAsync(
                $"Existing PostgreSQL settings for {applicationDirectory} are not usable: {ex.Message}");
            return null;
        }
    }

    internal static async Task PingAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand("SELECT 1", connection);
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        if (!Equals(result, 1))
        {
            throw new InvalidOperationException("PostgreSQL returned an unexpected health-check result.");
        }
    }

    internal static async Task WriteConnectionStringAsync(
        string applicationDirectory,
        string connectionString,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(applicationDirectory, "appsettings.Local.json");
        JsonObject root = ReadOrCreateObject(path);

        if (root["ConnectionStrings"] is not JsonObject connectionStrings)
        {
            connectionStrings = [];
            root["ConnectionStrings"] = connectionStrings;
        }

        connectionStrings["Postgres"] = connectionString;
        await File.WriteAllTextAsync(
            path,
            root.ToJsonString(LocalJsonOptions) + Environment.NewLine,
            Encoding.UTF8,
            cancellationToken);
        await Console.Out.WriteLineAsync($"Wrote PostgreSQL connection settings to {path}.");
    }

    private static JsonObject ReadOrCreateObject(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        string json = File.ReadAllText(path, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException($"File is not a JSON object: {path}");
    }
}
