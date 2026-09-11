using TelegramBot.Core.Config;

namespace TelegramBot.Worker.Services;

/// <summary>Explicit, mutually exclusive outcome of command preparation before a process starts.</summary>
public sealed class CommandPreparationResult
{
    private readonly CommandConfig? _configuration;

    public bool IsReady { get; }
    public string? ErrorMessage { get; }

    private CommandPreparationResult(CommandConfig? configuration, string? errorMessage)
    {
        _configuration = configuration;
        ErrorMessage = errorMessage;
        IsReady = configuration is not null;
    }

    public static CommandPreparationResult Ready(CommandConfig configuration) => new(configuration, null);

    public static CommandPreparationResult Failed(string errorMessage) => new(null, errorMessage);

    public CommandConfig GetConfiguration() => _configuration
        ?? throw new InvalidOperationException("A failed command preparation has no process configuration.");
}
