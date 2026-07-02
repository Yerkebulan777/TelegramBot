namespace TelegramBot.Core.Constants;

/// <summary>
/// Фильтры списка сессий в /status.
/// </summary>
public static class StatusFilters
{
    public const string All = "ALL";
    public const string Active = "ACTIVE";
    public const string Done = "DONE";
    public const string Failed = "FAILED";

    /// <summary>Заголовок по умолчанию.</summary>
    public const string AllTitle = "📋 Все сессии";

    private static readonly StatusFilterDescriptor[] s_descriptors =
    [
        new(All, AllTitle),
        new(Active, "🔄 Активные"),
        new(Done, "✅ Завершённые"),
        new(Failed, "❌ С ошибками")
    ];

    public static IReadOnlyList<StatusFilterDescriptor> AllDescriptors => s_descriptors;

    /// <summary>Возвращает заголовок по ключу фильтра; для неизвестного — AllTitle.</summary>
    public static string GetTitle(string filter)
    {
        return s_descriptors.FirstOrDefault(d => string.Equals(d.Key, filter, StringComparison.OrdinalIgnoreCase))?.Title
            ?? AllTitle;
    }
}

public sealed record StatusFilterDescriptor(string Key, string Title);
