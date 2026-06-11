using Serilog.Events;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Helper для Serilog-фильтрации событий из пространства имён TelegramBot.BimLib.*.
/// Используется с Filter.ByIncludingOnly() для выделения BIM-специфичных логов в отдельный файл.
/// </summary>
internal static class BimLibLogFilter
{
    private const string BimLibPrefix = "\"TelegramBot.BimLib";

    /// <summary>
    /// Фильтр: true если событие относится к BimLib (SourceContext содержит "TelegramBot.BimLib").
    /// Serilog оборачивает строковые ScalarValue в кавычки, поэтому проверяем с '"'.
    /// </summary>
    public static bool IsBimLibEvent(LogEvent logEvent)
    {
        return logEvent.Properties.TryGetValue("SourceContext", out var sourceContext)
               && sourceContext.ToString()?.StartsWith(BimLibPrefix, StringComparison.Ordinal) == true;
    }
}
