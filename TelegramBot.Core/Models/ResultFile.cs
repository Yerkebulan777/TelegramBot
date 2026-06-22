using System.Text.Json.Serialization;

namespace TelegramBot.Core.Models;

/// <summary>
/// Статус выполнения BIM-команды в result-файле.
/// Сериализуется в camelCase (done/failed/cancelled) через <see cref="JsonStringEnumConverter"/>.
/// </summary>
public enum ResultStatus
{
    /// <summary>Команда выполнена успешно.</summary>
    Done,

    /// <summary>Команда завершилась с ошибкой (permanent или transient — определяет <c>ErrorClassifier</c>).</summary>
    Failed,

    /// <summary>Команда была отменена (пользователем или системой). Трактовать как permanent failure без retry.</summary>
    Cancelled,
}

/// <summary>
/// Результат от BIM-плагина. Плагин пишет <c>result_{CommandId}_{AttemptToken}.json</c> в TaskDirectory.
/// Worker читает после завершения процесса. Если файла нет — статус определяется по exit code.
/// </summary>
/// <remarks>
/// Формат полностью соответствует <c>…\RevitBIMFusion\Docs\BimPluginContract.md</c> и
/// JSON-схеме <c>…\RevitBIMFusion\Docs\ResultFile.schema.json</c>.
/// </remarks>
public sealed class ResultFile
{
    /// <summary>
    /// Статус выполнения команды. Сериализуется как camelCase-строка (<c>"done"</c> / <c>"failed"</c> / <c>"cancelled"</c>)
    /// через <see cref="JsonStringEnumConverter"/>.
    /// </summary>
    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required ResultStatus Status { get; set; }

    /// <summary>
    /// Краткое сообщение об ошибке при <see cref="Status"/> = <c>Failed</c>. <c>null</c> при <c>Done</c>.
    /// </summary>
    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Полный stack trace / диагностика при неожиданных исключениях. <c>null</c> при <c>Done</c> и для ожидаемых сбоев.
    /// Worker логирует это поле при наличии для отладки.
    /// </summary>
    [JsonPropertyName("errorDetails")]
    public string? ErrorDetails { get; set; }

    /// <summary>
    /// Путь к выходному файлу при <see cref="Status"/> = <c>Done</c>. Несмотря на множественное число в имени,
    /// это **одна строка**, не массив — соответствует канону в <c>…\RevitBIMFusion\Docs\ResultFile.schema.json</c>
    /// и C# модели <c>WorkerBridge.Core.ResultFile</c>. Для PDF/NWC — путь к единственному выходному файлу,
    /// для DWG (один .dwg на лист) — путь к папке экспорта, а не список листов.
    /// <c>null</c> при <c>Failed</c>/<c>Cancelled</c> и при <c>Done</c> без выходных файлов.
    /// Поле опускается из JSON при <c>null</c> (WhenWritingNull).
    /// </summary>
    [JsonPropertyName("outputFiles")]
    public string? OutputFiles { get; set; }
}
