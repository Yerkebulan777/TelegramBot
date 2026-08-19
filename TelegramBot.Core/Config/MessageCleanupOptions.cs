namespace TelegramBot.Core.Config;

/// <summary>
/// Настройки фоновой очистки устаревших сообщений Telegram.
/// </summary>
public sealed class MessageCleanupOptions
{
    public const string SectionName = "MessageCleanup";

    /// <summary>Включает фоновую очистку сообщений.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Интервал запуска очистки в минутах.</summary>
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>Возраст сообщения в часах, после которого оно подлежит удалению.</summary>
    public int RetentionHours { get; set; } = 24;

    /// <summary>
    /// Максимальный возраст сообщения для попытки удаления. Должен оставлять запас до лимита Telegram в 48 часов.
    /// </summary>
    public int MaximumDeletionAgeHours { get; set; } = 47;

    /// <summary>Максимум tracking-записей за один цикл.</summary>
    public int BatchSize { get; set; } = 500;
}
