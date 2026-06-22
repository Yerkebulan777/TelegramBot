namespace TelegramBot.Core.Config;

/// <summary>Конфигурация одной команды: исполняемый файл, шаблон аргументов, разрешённые расширения.</summary>
public sealed class CommandConfig
{
    /// <summary>Путь к исполняемому файлу (например, "Revit.exe" или "python").</summary>
    public string ExecutablePath { get; set; } = "";

    /// <summary>
    /// Шаблон аргументов командной строки.
    /// Поддерживаемые плейсхолдеры: {CommandText}, {FilePath}, {CommandId}, {TaskFilePath}, {ResultFilePath}.
    /// Для RevitBIMFusion Revit AddIn вместо {CommandText} должен передаваться fixed dispatcher WORKER;
    /// реальная команда остаётся в TaskFile.commandText.
    /// </summary>
    public string ArgumentsTemplate { get; set; } = "\"{FilePath}\"";

    /// <summary>Разрешённые расширения файлов (например, .rvt, .rfa). null — любое расширение.</summary>
    public List<string>? AllowedExtensions { get; set; }

    /// <summary>Рабочая директория. Если null — используется папка обрабатываемого файла. "." — корень процесса.</summary>
    public string? WorkingDirectory { get; set; }
}
