namespace TelegramBot.Core.Constants;

/// <summary>
/// Константы статусов команд и сессий.
/// </summary>
public static class CommandStatuses
{
    /// <summary>Команда ожидает выполнения.</summary>
    public const string Pending = "pending";

    /// <summary>Команда выполняется (захвачена воркером).</summary>
    public const string Processing = "processing";

    /// <summary>Команда успешно выполнена.</summary>
    public const string Done = "Done";

    /// <summary>Команда завершилась ошибкой.</summary>
    public const string Failed = "Failed";

    /// <summary>Команда/сессия мягко удалена.</summary>
    public const string Deleted = "Deleted";

    /// <summary>Статусы, считающиеся финальными (команда больше не может быть изменена).</summary>
    public static readonly IReadOnlySet<string> FinalStatuses = new HashSet<string>([Done, Failed, Deleted]);

    /// <summary>Статусы, в которых команда активна (ожидает или выполняется).</summary>
    public static readonly IReadOnlySet<string> ActiveStatuses = new HashSet<string>([Pending, Processing]);
}
