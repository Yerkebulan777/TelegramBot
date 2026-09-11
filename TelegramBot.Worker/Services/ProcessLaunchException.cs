namespace TelegramBot.Worker.Services;

/// <summary>
/// The shared launch gate could not complete a process launch.
/// This is an execution failure, not a command-status persistence failure, so the normal retry policy applies.
/// </summary>
public sealed class ProcessLaunchException(string product, int commandId, Exception innerException)
    : Exception($"Could not acquire the shared {product} launch gate for command {commandId}.", innerException);
