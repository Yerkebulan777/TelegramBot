namespace TelegramBot.Worker.Models;

/// <summary>Ответ команды AutoBIMFusion <c>MERGEDWG_BATCH</c> (status JSON).</summary>
public sealed record MergeDwgBatchStatus
{
    public bool Success { get; init; }
    public string? FolderPath { get; init; }
    public string? SavePath { get; init; }
    public string? Message { get; init; }
    public string? LogPath { get; init; }
}
