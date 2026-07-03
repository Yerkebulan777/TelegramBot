using System.Xml.Serialization;

namespace TelegramBot.Core.Models;

/// <summary>
/// Файл задания для BIM-плагина. Worker создаёт <c>task_{projectName}_{commandId}.xml</c> в TaskDirectory
/// перед запуском процесса. Плагин читает этот файл, получает все параметры команды и после
/// выполнения пишет результат в <c>result_{projectName}_{commandId}.xml</c>.
/// </summary>
/// <remarks>
/// Формат полностью соответствует <c>…\RevitBIMFusion\Docs\BimPluginContract.md</c> и
/// XML-схеме <c>…\RevitBIMFusion\Docs\TaskFile.schema.xsd</c>.
/// Плагин НЕ должен полагаться только на аргументы командной строки —
/// task-файл содержит полную и структурированную информацию о задании.
/// </remarks>
[XmlRoot("taskFile")]
public sealed class TaskFile
{
    /// <summary>ID команды в БД (соответствует CommandId в Commands таблице).</summary>
    [XmlElement("commandId")]
    public required int CommandId { get; set; }

    /// <summary>
    /// Тип команды (код экспорта): <c>"PDF"</c>, <c>"DWG"</c>, <c>"IFC"</c>, <c>"BIMDOC"</c>,
    /// <c>"NWC"</c>, <c>"CLASHREP"</c>, <c>"AUTORES"</c>.
    /// Соответствует <see cref="Constants.CommandCodes"/>.
    /// </summary>
    [XmlElement("commandText")]
    public required string CommandText { get; set; }

    /// <summary>
    /// Полный путь к исходному файлу (.rvt, .rfa, .nwc, .nwd, .ifc и т.д.).
    /// AddIn открывает файл сам — через <c>OpenOptions { Audit = true, DetachAndPreserveWorksets }</c> для .rvt.
    /// </summary>
    [XmlElement("filePath")]
    public required string FilePath { get; set; }

    /// <summary>
    /// Полный путь к файлу результата в TaskDirectory. Плагин обязан записать сюда XML с
    /// <see cref="ResultFile"/> (см. <c>…\RevitBIMFusion\Docs\ResultFile.schema.xsd</c>).
    /// </summary>
    [XmlElement("resultFilePath")]
    public required string ResultFilePath { get; set; }

    /// <summary>
    /// Дополнительные опции команды. Closed whitelist — в текущей реализации поддерживается только
    /// <c>continueOnError</c> (bool) для PDF/DWG. Неизвестные ключи находятся вне контракта.
    /// Сериализуется как XML-элемент <c>options</c> или опускается.
    /// </summary>
    [XmlElement("options")]
    public TaskFileOptions? Options { get; set; }
}

public sealed class TaskFileOptions
{
    [XmlElement("continueOnError")]
    public bool ContinueOnError { get; set; }

    [XmlIgnore]
    public bool ContinueOnErrorSpecified { get; set; }
}
