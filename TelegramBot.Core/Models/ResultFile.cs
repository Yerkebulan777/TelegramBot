using System.Xml.Serialization;

namespace TelegramBot.Core.Models;

/// <summary>
/// Статус выполнения BIM-команды в result-файле.
/// </summary>
public enum ResultStatus
{
    /// <summary>Внутренний sentinel для отсутствующего обязательного XML-элемента status.</summary>
    Unknown,

    /// <summary>Команда выполнена успешно.</summary>
    [XmlEnum("done")]
    Done,

    /// <summary>
    /// Ошибка экспорта (plugin origin → permanent <c>Failed</c> без retry).
    /// Отсутствие ResultFile / битый XML идут другим путём (retry policy).
    /// </summary>
    [XmlEnum("failed")]
    Failed,

    /// <summary>Отмена плагином → permanent <c>Failed</c> без retry.</summary>
    [XmlEnum("cancelled")]
    Cancelled,
}

/// <summary>
/// Результат от BIM-плагина. Плагин пишет <c>result_{projectName}_{commandId}.xml</c>.
/// Для Revit ResultFile обязателен; exit-code fallback — только wrapper-командам.
/// </summary>
/// <remarks>
/// Эталон: <c>RevitBIMFusion/Docs/BimPluginContract.md</c> (v2026-09-09) и
/// <c>Docs/BimContract/ResultFile.schema.xsd</c> (vendored).
/// </remarks>
[XmlRoot("resultFile")]
public sealed class ResultFile
{
    /// <summary><c>done</c> / <c>failed</c> / <c>cancelled</c>.</summary>
    [XmlElement("status")]
    public required ResultStatus Status { get; set; }

    /// <summary>Короткое сообщение для пользователя при <see cref="ResultStatus.Failed"/>.</summary>
    [XmlElement("errorMessage")]
    public string? ErrorMessage { get; set; }

    /// <summary>Предупреждение при <see cref="ResultStatus.Done"/> (экспорт успешен, были восстанавливаемые проблемы).</summary>
    [XmlElement("warningMessage")]
    public string? WarningMessage { get; set; }

    /// <summary>Stack trace / диагностика при неожиданных исключениях.</summary>
    [XmlElement("errorDetails")]
    public string? ErrorDetails { get; set; }

    /// <summary>
    /// Одна строка: файл (PDF/NWC/IFC/DATA) или папка (DWG). Не массив.
    /// </summary>
    [XmlElement("outputFiles")]
    public string? OutputFiles { get; set; }

    /// <summary>
    /// Уникальная временная директория экспорта, которую Worker удаляет после завершения Revit.
    /// Отсутствует при fresh-skip, RESAVE и ошибке до выделения директории.
    /// </summary>
    [XmlElement("temporaryDirectoryPath")]
    public string? TemporaryDirectoryPath { get; set; }

    /// <summary>Время выполнения в мс (все статусы). Пишет AddIn; опционально по XSD.</summary>
    [XmlElement("executionTimeMilliseconds")]
    public long? ExecutionTimeMilliseconds { get; set; }
}
