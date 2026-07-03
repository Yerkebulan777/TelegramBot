using System.Reflection;
using System.Xml;
using System.Xml.Schema;

namespace TelegramBot.Worker.Schemas;

/// <summary>
/// Runtime-валидация task-файла по эталонной XSD из <c>RevitBIMFusion/Docs</c>, embedded resource.
/// Ловит drift между C#-моделью <c>TaskFile</c> и XML-контрактом до того, как файл попадёт
/// в TaskDirectory к плагину. Используется <c>CommandPreparer.CreateTaskFile</c>.
/// </summary>
public static class TaskFileValidator
{
    private const string ManifestResourceName = "TelegramBot.Worker.Schemas.TaskFile.schema.xsd";

    private static readonly Lazy<XmlSchemaSet> Schemas = new(LoadSchemaSet, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Валидирует XML-документ по схеме TaskFile.
    /// </summary>
    /// <param name="reader"><c>XmlReader</c> над task-файлом. Должен быть в начальной позиции.</param>
    /// <returns>Список ошибок валидации. Пустой список — валидный документ.</returns>
    public static List<string> Validate(XmlReader reader)
    {
        var settings = new XmlReaderSettings
        {
            ValidationType = ValidationType.Schema,
            Schemas = Schemas.Value,
            ValidationFlags = XmlSchemaValidationFlags.ReportValidationWarnings,
        };

        var errors = new List<string>();
        settings.ValidationEventHandler += (_, e) =>
        {
            // Severity Error — нарушение контракта; Warning — напр. unmatched elements (не критично, но логируем).
            errors.Add($"{e.Severity}: {e.Message}");
        };

        // Полностью читаем validating reader, чтобы сработали все validation-события.
        using var validatingReader = XmlReader.Create(reader, settings);
        while (validatingReader.Read()) { }

        return errors;
    }

    private static XmlSchemaSet LoadSchemaSet()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ManifestResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded XSD resource not found: {ManifestResourceName}. " +
                "Ensure RevitBIMFusion/Docs/TaskFile.schema.xsd is configured as EmbeddedResource in TelegramBot.Worker.csproj.");

        var schema = XmlSchema.Read(stream, (_, e) =>
            throw new InvalidOperationException($"TaskFile.schema.xsd is itself invalid: {e.Message}"))
            ?? throw new InvalidOperationException("XmlSchema.Read returned null for TaskFile.schema.xsd");

        var set = new XmlSchemaSet();
        set.Add(schema);
        set.Compile();
        return set;
    }
}
