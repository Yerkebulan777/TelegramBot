using TelegramBot.Installer;

if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
{
    PrintUsage();
    return 2;
}

try
{
    if (string.Equals(args[0], "check", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            PrintUsage();
            return 2;
        }

        await ApplicationSettings.PingDirectoryAsync(Path.GetFullPath(args[1]), CancellationToken.None);
        await Console.Out.WriteLineAsync("PostgreSQL connection check succeeded.");
        return 0;
    }

    if (string.Equals(args[0], "ensure", StringComparison.OrdinalIgnoreCase))
    {
        return await PostgresEnsureCommand.RunAsync(ParseEnsureArguments(args));
    }

    PrintUsage();
    return 2;
}
catch (Exception ex)
{
    await Console.Error.WriteLineAsync(ex.Message);
    return 1;
}

static PostgresEnsureArguments ParseEnsureArguments(string[] arguments)
{
    string? serverDirectory = null;
    string? workerDirectory = null;

    for (var i = 1; i < arguments.Length; i++)
    {
        string key = arguments[i];
        if (++i >= arguments.Length || string.IsNullOrWhiteSpace(arguments[i]) || arguments[i].StartsWith('-'))
        {
            throw new InvalidOperationException("Expected argument: " + key + " <directory>");
        }

        string value = arguments[i];
        if (string.Equals(key, "--server", StringComparison.OrdinalIgnoreCase))
        {
            serverDirectory = value;
        }
        else if (string.Equals(key, "--worker", StringComparison.OrdinalIgnoreCase))
        {
            workerDirectory = value;
        }
        else
        {
            throw new InvalidOperationException("Unknown ensure argument: " + key);
        }
    }

    return new PostgresEnsureArguments(serverDirectory, workerDirectory);
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  PostgresConnectionCheck check <application-directory>");
    Console.Error.WriteLine("  PostgresConnectionCheck ensure [--server <dir>] [--worker <dir>]");
}
