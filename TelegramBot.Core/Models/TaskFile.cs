using System.Xml.Serialization;

namespace TelegramBot.Core.Models;

/// <summary>
/// Файл задания для BIM-плагина. Worker создаёт <c>task_{projectName}_{commandId}.xml</c> в TaskDirectory
/// перед запуском процесса. Revit AddIn получает путь через <c>REVITBIMFUSION_TASK_FILE</c>.
/// </summary>
/// <remarks>
/// Эталон: <c>RevitBIMFusion/Docs/BimPluginContract.md</c> (v2026-09-09) и
/// <c>Docs/BimContract/TaskFile.schema.xsd</c> (vendored).
/// </remarks>
[XmlRoot("taskFile")]
public sealed class TaskFile
{
    /// <summary>ID команды в БД (опционально по XSD).</summary>
    [XmlElement("commandId")]
    public required int CommandId { get; set; }

    /// <summary>
    /// Канон Revit AddIn: <c>PDF</c>, <c>DWG</c>, <c>NWC</c>, <c>IFC</c>, <c>DATA</c>, <c>RESAVE</c> (регистр не важен).
    /// </summary>
    [XmlElement("commandText")]
    public required string CommandText { get; set; }

    /// <summary>
    /// Абсолютный путь к <c>.rvt</c>. AddIn открывает сам (<c>Audit</c>, detach).
    /// </summary>
    [XmlElement("filePath")]
    public required string FilePath { get; set; }

    /// <summary>
    /// Абсолютный путь к ResultFile. AddIn не вычисляет путь сам.
    /// </summary>
    [XmlElement("resultFilePath")]
    public required string ResultFilePath { get; set; }

    /// <summary>Зарезервировано; сейчас пусто (контрактный sample — <c>&lt;options /&gt;</c>).</summary>
    [XmlElement("options")]
    public TaskFileOptions? Options { get; set; }
}

/// <summary>Пустой контейнер <c>options</c> — зарезервирован, дочерних элементов нет.</summary>
public sealed class TaskFileOptions
{
}
