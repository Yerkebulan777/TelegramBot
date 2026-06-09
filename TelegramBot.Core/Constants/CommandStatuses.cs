namespace TelegramBot.Core.Constants;

/// <summary>
/// Устаревший класс. Используйте <see cref="Statuses"/> вместо этого.
/// </summary>
[Obsolete("Use Statuses instead")]
public static class CommandStatuses
{
    public const string Pending = Statuses.Pending;
    public const string Processing = Statuses.Processing;
    public const string Done = Statuses.Done;
    public const string Failed = Statuses.Failed;
    public const string Deleted = Statuses.Deleted;
    public static readonly IReadOnlySet<string> FinalStatuses = Statuses.FinalStatuses;
    public static readonly IReadOnlySet<string> ActiveStatuses = Statuses.ActiveStatuses;
}
