using System.Reflection;
using System.Xml;
using System.Xml.Schema;

namespace TelegramBot.Worker.Schemas;

/// <summary>
/// Runtime-валидация result-файла по эталонной XSD из <c>RevitBIMFusion/Docs</c>, embedded resource.
/// Ловит schema-invalid ответ плагина до десериализации в <c>ResultFile</c>.
/// </summary>
public static class ResultFileValidator
{
    private const string ManifestResourceName = "TelegramBot.Worker.Schemas.ResultFile.schema.xsd";

    private static readonly Lazy<XmlSchemaSet> Schemas = new(LoadSchemaSet, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Валидирует XML-документ по схеме ResultFile.</summary>
    /// <param name="reader"><c>XmlReader</c> над result-файлом в начальной позиции.</param>
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
        settings.ValidationEventHandler += (_, e) => errors.Add($"{e.Severity}: {e.Message}");

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
                "Ensure RevitBIMFusion/Docs/ResultFile.schema.xsd is configured as EmbeddedResource in TelegramBot.Worker.csproj.");

        var schema = XmlSchema.Read(stream, (_, e) =>
            throw new InvalidOperationException($"ResultFile.schema.xsd is itself invalid: {e.Message}"))
            ?? throw new InvalidOperationException("XmlSchema.Read returned null for ResultFile.schema.xsd");

        var set = new XmlSchemaSet();
        _ = set.Add(schema);
        set.Compile();
        return set;
    }
}
