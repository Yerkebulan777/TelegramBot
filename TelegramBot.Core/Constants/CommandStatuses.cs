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
}
