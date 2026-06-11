using System.Text.Json.Serialization;

namespace TelegramBot.Core.Models;

/// <summary>
/// Результат от CAD-плагина. Плагин пишет <c>result_{CommandId}_{AttemptToken}.json</c> во временную папку.
/// Worker читает после завершения процесса. Если файла нет — статус определяется по exit code.
/// </summary>
public sealed class ResultFile
{
    /// <summary>
    /// Статус: <c>"done"</c> или <c>"failed"</c>.
    /// </summary>
    [JsonPropertyName("status")]
    public required string Status { get; set; }

    /// <summary>Сообщение об ошибке (при status = "failed").</summary>
    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    /// <summary>Список сгенерированных файлов (при status = "done").</summary>
    [JsonPropertyName("outputFiles")]
    public List<string> OutputFiles { get; set; } = [];
}
