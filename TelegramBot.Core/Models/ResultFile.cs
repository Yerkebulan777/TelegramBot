using System.Xml.Serialization;

namespace TelegramBot.Core.Models;

/// <summary>
/// Статус выполнения BIM-команды в result-файле.
/// </summary>
public enum ResultStatus
{
    /// <summary>Команда выполнена успешно.</summary>
    [XmlEnum("done")]
    Done,

    /// <summary>Команда завершилась с ошибкой (permanent или transient — определяет <c>ErrorClassifier</c>).</summary>
    [XmlEnum("failed")]
    Failed,

    /// <summary>Команда была отменена (пользователем или системой). Трактовать как permanent failure без retry.</summary>
    [XmlEnum("cancelled")]
    Cancelled,
}

/// <summary>
/// Результат от BIM-плагина. Плагин пишет <c>result_{CommandId}_{AttemptToken}.xml</c> в TaskDirectory.
/// Worker читает после завершения процесса. Если файла нет — статус определяется по exit code.
/// </summary>
/// <remarks>
/// Формат полностью соответствует <c>…\RevitBIMFusion\Docs\BimPluginContract.md</c> и
/// XML-схеме <c>…\RevitBIMFusion\Docs\ResultFile.schema.xsd</c>.
/// </remarks>
[XmlRoot("resultFile")]
public sealed class ResultFile
{
    /// <summary>
    /// Статус выполнения команды. Сериализуется как строка <c>done</c> / <c>failed</c> / <c>cancelled</c>.
    /// </summary>
    [XmlElement("status")]
    public required ResultStatus Status { get; set; }

    /// <summary>
    /// Краткое сообщение об ошибке при <see cref="Status"/> = <c>Failed</c>. <c>null</c> при <c>Done</c>.
    /// </summary>
    [XmlElement("errorMessage")]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Полный stack trace / диагностика при неожиданных исключениях. <c>null</c> при <c>Done</c> и для ожидаемых сбоев.
    /// Worker логирует это поле при наличии для отладки.
    /// </summary>
    [XmlElement("errorDetails")]
    public string? ErrorDetails { get; set; }

    /// <summary>
    /// Путь к выходному файлу при <see cref="Status"/> = <c>Done</c>. Несмотря на множественное число в имени,
    /// это **одна строка**, не массив — соответствует канону в <c>…\RevitBIMFusion\Docs\ResultFile.schema.xsd</c>
    /// и C# модели <c>WorkerBridge.Core.ResultFile</c>. Для PDF/NWC — путь к единственному выходному файлу,
    /// для DWG (один .dwg на лист) — путь к папке экспорта, а не список листов.
    /// <c>null</c> при <c>Failed</c>/<c>Cancelled</c> и при <c>Done</c> без выходных файлов.
    /// XML-элемент опускается при <c>null</c>.
    /// </summary>
    [XmlElement("outputFiles")]
    public string? OutputFiles { get; set; }
}
