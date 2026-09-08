namespace TelegramBot.Core.Models;

/// <summary>Skipped command/file pair with a snapshot of the existing active command.</summary>
public sealed class CommandConflict
{
    public required string Command { get; init; }
    public required string FilePath { get; init; }
    public int? CommandId { get; init; }
    public int? SessionId { get; init; }
    public string? Status { get; init; }
    public DateTime? CreatedAt { get; init; }
}
