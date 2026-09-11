namespace TelegramBot.Worker.Services;

/// <summary>
/// The shared Revit launch gate could not complete a launch.
/// This is an execution failure, not a command-status persistence failure, so the normal retry policy applies.
/// </summary>
public sealed class RevitLaunchException(int commandId, Exception innerException)
    : Exception($"Could not acquire the shared Revit launch gate for command {commandId}.", innerException);
