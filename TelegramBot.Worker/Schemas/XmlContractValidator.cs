using System.Reflection;
using System.Xml;
using System.Xml.Schema;

namespace TelegramBot.Worker.Schemas;

/// <summary>
/// Runtime-валидация TaskFile/ResultFile по эталонным XSD, embedded resource.
/// </summary>
public static class XmlContractValidator
{
    private static readonly Lazy<XmlSchemaSet> TaskFileSchemas = new(
        () => LoadSchemaSet(
            "TelegramBot.Worker.Schemas.TaskFile.schema.xsd",
            "TaskFile.schema.xsd"),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<XmlSchemaSet> ResultFileSchemas = new(
        () => LoadSchemaSet(
            "TelegramBot.Worker.Schemas.ResultFile.schema.xsd",
            "ResultFile.schema.xsd"),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Валидирует XML-документ по схеме TaskFile.</summary>
    public static List<string> ValidateTaskFile(XmlReader reader) => Validate(reader, TaskFileSchemas.Value);

    /// <summary>Валидирует XML-документ по схеме ResultFile.</summary>
    public static List<string> ValidateResultFile(XmlReader reader) => Validate(reader, ResultFileSchemas.Value);

    private static List<string> Validate(XmlReader reader, XmlSchemaSet schemas)
    {
        var settings = new XmlReaderSettings
        {
            ValidationType = ValidationType.Schema,
            Schemas = schemas,
            ValidationFlags = XmlSchemaValidationFlags.ReportValidationWarnings,
        };

        var errors = new List<string>();
        settings.ValidationEventHandler += (_, e) => errors.Add($"{e.Severity}: {e.Message}");

        using var validatingReader = XmlReader.Create(reader, settings);
        while (validatingReader.Read()) { }

        return errors;
    }

    private static XmlSchemaSet LoadSchemaSet(string resourceName, string schemaFileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded XSD resource not found: {resourceName}. " +
                $"Ensure RevitBIMFusion/Docs/{schemaFileName} is configured as EmbeddedResource in TelegramBot.Worker.csproj.");

        var schema = XmlSchema.Read(stream, (_, e) =>
            throw new InvalidOperationException($"{schemaFileName} is itself invalid: {e.Message}"))
            ?? throw new InvalidOperationException($"XmlSchema.Read returned null for {schemaFileName}");

        var set = new XmlSchemaSet();
        _ = set.Add(schema);
        set.Compile();
        return set;
    }
}
