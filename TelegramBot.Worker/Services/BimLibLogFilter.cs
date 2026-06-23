using Serilog.Events;

namespace TelegramBot.Worker.Services;

/// <summary>
/// Helper для Serilog-фильтрации событий из пространства имён TelegramBot.Worker.BimLib.*.
/// Используется с Filter.ByIncludingOnly() для выделения BIM-специфичных логов в отдельный файл.
/// </summary>
internal static class BimLibLogFilter
{
    /// <summary>
    /// Фильтр: true если событие относится к BimLib (SourceContext содержит "TelegramBot.Worker.BimLib").
    /// </summary>
    public static bool IsBimLibEvent(LogEvent logEvent)
    {
        return logEvent.Properties.TryGetValue("SourceContext", out var sc)
               && sc is ScalarValue { Value: string s }
               && s.StartsWith("TelegramBot.Worker.BimLib", StringComparison.Ordinal);
    }
}
