namespace TelegramBot.Data;

/// <summary>A database write failed; this must not be classified as a BIM process failure.</summary>
public sealed class CommandPersistenceException(int commandId, Exception innerException)
    : Exception($"Could not persist the outcome of command {commandId}.", innerException);
