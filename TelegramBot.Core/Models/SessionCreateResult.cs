namespace TelegramBot.Core.Models;

public enum SessionCreateStatus
{
    Created,
    AllDuplicates,
    DailyLimitExceeded,
}

/// <summary>Исход атомарного создания Session+Commands.</summary>
public sealed class SessionCreateResult
{
    public SessionCreateStatus Status { get; private init; }
    public int? SessionId { get; private init; }
    public int QueuedFileCount { get; private init; }
    public IReadOnlyList<CommandConflict> SkippedPairs { get; private init; } = [];
    public int DailyLimitQueuedToday { get; private init; }

    public static SessionCreateResult Created(
        int sessionId, int queuedFileCount, IReadOnlyList<CommandConflict> skippedPairs) =>
        new()
        {
            Status = SessionCreateStatus.Created,
            SessionId = sessionId,
            QueuedFileCount = queuedFileCount,
            SkippedPairs = skippedPairs,
        };

    public static SessionCreateResult AllDuplicates(IReadOnlyList<CommandConflict> skippedPairs) =>
        new()
        {
            Status = SessionCreateStatus.AllDuplicates,
            SkippedPairs = skippedPairs,
        };

    public static SessionCreateResult DailyLimitExceeded(int queuedToday) =>
        new()
        {
            Status = SessionCreateStatus.DailyLimitExceeded,
            DailyLimitQueuedToday = queuedToday,
        };
}
