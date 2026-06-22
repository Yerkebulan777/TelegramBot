using System.Text.Json;
using System.Text.Json.Serialization;

namespace TelegramBot.Core.Models;

/// <summary>
/// Файл задания для BIM-плагина. Worker создаёт <c>task_{CommandId}_{AttemptToken}.json</c> в TaskDirectory
/// перед запуском процесса. Плагин читает этот файл, получает все параметры команды и после
/// выполнения пишет результат в <c>result_{CommandId}_{AttemptToken}.json</c>.
/// </summary>
/// <remarks>
/// Формат полностью соответствует <c>…\RevitBIMFusion\Docs\BimPluginContract.md</c> и
/// JSON-схеме <c>…\RevitBIMFusion\Docs\TaskFile.schema.json</c>.
/// Плагин НЕ должен полагаться только на аргументы командной строки —
/// task-файл содержит полную и структурированную информацию о задании.
/// </remarks>
public sealed class TaskFile
{
    /// <summary>ID команды в БД (соответствует CommandId в Commands таблице).</summary>
    [JsonPropertyName("commandId")]
    public required int CommandId { get; set; }

    /// <summary>
    /// Тип команды (код экспорта): <c>"PDF"</c>, <c>"DWG"</c>, <c>"IFC"</c>, <c>"BIMDOC"</c>,
    /// <c>"NWC"</c>, <c>"CLASHREP"</c>, <c>"AUTORES"</c>.
    /// Соответствует <see cref="Constants.CommandCodes"/>.
    /// </summary>
    [JsonPropertyName("commandText")]
    public required string CommandText { get; set; }

    /// <summary>
    /// Полный путь к исходному файлу (.rvt, .rfa, .nwc, .nwd, .ifc и т.д.).
    /// AddIn открывает файл сам — через <c>OpenOptions { Audit = true, DetachAndPreserveWorksets }</c> для .rvt.
    /// </summary>
    [JsonPropertyName("filePath")]
    public required string FilePath { get; set; }

    /// <summary>
    /// Полный путь к файлу результата в TaskDirectory. Плагин обязан записать сюда JSON с
    /// <see cref="ResultFile"/> (см. <c>…\RevitBIMFusion\Docs\ResultFile.schema.json</c>).
    /// </summary>
    [JsonPropertyName("resultFilePath")]
    public required string ResultFilePath { get; set; }

    /// <summary>
    /// Дополнительные опции команды. Closed whitelist — в текущей реализации поддерживается только
    /// <c>continueOnError</c> (bool) для PDF/DWG. Неизвестные ключи находятся вне контракта.
    /// Сериализуется как JSON-объект или <c>null</c>.
    /// </summary>
    [JsonPropertyName("options")]
    public JsonElement? Options { get; set; }
}
