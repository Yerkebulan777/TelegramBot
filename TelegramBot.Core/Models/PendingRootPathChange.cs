namespace TelegramBot.Core.Models;

/// <summary>
/// Заявка на смену корневого UNC-пути, подготовленная из интерактивной сессии Windows.
/// </summary>
public sealed record PendingRootPathChange(
    Guid Id,
    string UncPath,
    DateTimeOffset CreatedAtUtc,
    string Status)
{
    public bool IsPending => string.Equals(Status, PendingStatus, StringComparison.Ordinal);

    public const string PendingStatus = "pending";
    public const string AppliedStatus = "applied";
    public const string CancelledStatus = "cancelled";
}
