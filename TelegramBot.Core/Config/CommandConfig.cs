namespace TelegramBot.Core.Config;

/// <summary>Конфигурация одной команды: исполняемый файл, шаблон аргументов, разрешённые расширения.</summary>
public sealed class CommandConfig
{
    /// <summary>Путь к исполняемому файлу (например, "Revit.exe" или "python").</summary>
    public string ExecutablePath { get; set; } = "";

    /// <summary>
    /// Шаблон аргументов командной строки. Может быть пустым.
    /// Поддерживаемые плейсхолдеры: {CommandText}, {FilePath}, {CommandId}, {TaskFilePath}, {ResultFilePath}.
    /// Для RevitBIMFusion шаблон игнорируется: без контрактных CLI-аргументов,
    /// TaskFile через process-scoped <c>REVITBIMFUSION_TASK_FILE</c>, плюс <c>/language RUS</c>.
    /// </summary>
    public string ArgumentsTemplate { get; set; } = "";

    /// <summary>Разрешённые расширения файлов (например, .rvt, .rfa). null — любое расширение.</summary>
    public List<string>? AllowedExtensions { get; set; }

    /// <summary>Рабочая директория. Если null — используется папка обрабатываемого файла. "." — корень процесса.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Копия с подставленными exe и аргументами для одного запуска.
    /// Shared-объект из <c>IOptions</c> мутировать нельзя: параллельные команды «загрязняют»
    /// конфигурацию друг друга.
    /// https://learn.microsoft.com/en-us/dotnet/core/extensions/options#ios-postconfigure-options
    /// </summary>
    public CommandConfig WithResolved(string executablePath, string argumentsTemplate) => new()
    {
        ExecutablePath = executablePath,
        ArgumentsTemplate = argumentsTemplate,
        AllowedExtensions = AllowedExtensions is null ? null : [.. AllowedExtensions],
        WorkingDirectory = WorkingDirectory,
    };
}
