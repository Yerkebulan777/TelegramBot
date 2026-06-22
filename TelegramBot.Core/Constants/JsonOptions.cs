using System.Text.Json;
using System.Text.Json.Serialization;

namespace TelegramBot.Core.Constants;

/// <summary>
/// Общие настройки JSON для всего проекта (TaskFile/ResultFile обмен с BIM-плагинами).
/// Используется в <c>CommandPreparer.CreateTaskFile</c> и <c>ProcessRunner.TryReadResultFile</c> —
/// контракт с плагинами требует camelCase + enum-as-string для ResultStatus.
/// </summary>
public static class JsonOptions
{
    public static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
