using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;

if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.Error.WriteLine("Usage: PostgresConnectionCheck <application-directory>");
    return 2;
}

string applicationDirectory = Path.GetFullPath(args[0]);

try
{
    string environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
        ?? Environments.Production;

    IConfigurationRoot configuration = new ConfigurationBuilder()
        .SetBasePath(applicationDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
        .AddJsonFile("appsettings.Local.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    string connectionString = configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException(
            "ConnectionStrings:Postgres is not configured in the application settings.");

    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    await using var command = new NpgsqlCommand("SELECT 1", connection);
    object? result = await command.ExecuteScalarAsync();
    if (!Equals(result, 1))
    {
        throw new InvalidOperationException("PostgreSQL returned an unexpected health-check result.");
    }

    Console.WriteLine("PostgreSQL connection check succeeded.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
