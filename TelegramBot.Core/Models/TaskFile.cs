namespace TelegramBot.Core.Models;

/// <summary>
/// Файл задания для CAD-плагина. Worker создаёт <c>task_{CommandId}_{AttemptToken}.json</c> во временной папке
/// перед запуском процесса. Плагин читает этот файл, получает все параметры команды и после
/// выполнения пишет результат в <c>result_{CommandId}_{AttemptToken}.json</c>.
/// </summary>
/// <remarks>
/// Это основной механизм обмена данными между Worker и CAD-плагином.
/// Плагин НЕ должен полагаться только на аргументы командной строки —
/// task-файл содержит полную и структурированную информацию о задании.
/// </remarks>
public sealed class TaskFile
{
    /// <summary>ID команды в БД (соответствует CommandId в Commands таблице).</summary>
    public required int CommandId { get; set; }

    /// <summary>
    /// Тип команды (код экспорта): <c>"PDF"</c>, <c>"DWG"</c>, <c>"IFC"</c>, <c>"BIMDOC"</c>,
    /// <c>"NWC"</c>, <c>"CLASHREP"</c>, <c>"AUTORES"</c>.
    /// Соответствует <see cref="Constants.CommandCodes"/>.
    /// </summary>
    public required string CommandText { get; set; }

    /// <summary>Полный путь к исходному файлу (.rvt, .rfa, .nwc, .nwd, .ifc и т.д.).</summary>
    public required string FilePath { get; set; }

    /// <summary>
    /// Полный путь к файлу результата. Плагин должен записать сюда JSON c полями
    /// <c>status</c> (<c>"done"</c> или <c>"failed"</c>), опционально <c>errorMessage</c>
    /// и <c>outputFiles</c> (см. <see cref="ResultFile"/>).
    /// </summary>
    public required string ResultFilePath { get; set; }

    /// <summary>
    /// Дополнительные опции для плагина.
    /// </summary>
    public Dictionary<string, string>? Options { get; set; }
}
