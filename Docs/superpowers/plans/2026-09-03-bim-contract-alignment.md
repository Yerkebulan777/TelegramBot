# BIM contract alignment implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `RESAVE` a Revit automation command with priority 3 and reject ResultFile XML that violates the vendored contract schema.

**Architecture:** `RESAVE` joins the existing data-driven command catalogue and the existing Revit command predicate, so it uses the established TaskFile, environment-variable handoff, startup arguments, result analysis, and cleanup. A static `ResultFileValidator` mirrors the existing task validator; it validates a separate stream before deserialization, preserving the current invalid-result and retry paths.

**Tech Stack:** C# 12, .NET 10, `XmlReader`/`XmlSchemaSet`, `XmlSerializer`, Microsoft.Extensions options, PowerShell.

## Global Constraints

- Canonical source: `../RevitBIMFusion/Docs/BimPluginContract.md`, version `2026-09-03`; do not copy or edit it.
- Vendored XSD files remain in `Docs/BimContract/`; embed the existing ResultFile schema in Worker.
- `RESAVE` belongs to `CommandGroup.Automation` and has `CommandPriorities.Medium` (3).
- Existing Worker rules forbid adding a test project or tests; use the schema script and full solution build as verification.
- Before editing a class or method, run GitNexus upstream impact analysis and warn for HIGH/CRITICAL risk.

---

### Task 1: Expose RESAVE through Telegram and Worker configuration

**Files:**
- Modify: `TelegramBot.Core/Constants/CommandCodes.cs:6-11`
- Modify: `TelegramBot.Core/Constants/CallbackPrefixes.cs:29-30`
- Modify: `TelegramBot.Server/Models/CommandDefinition.cs:21-27`
- Modify: `TelegramBot.Server/Services/Application/SlashCommandService.cs:359-368`
- Modify: `TelegramBot.Worker/appsettings.json:61-92`
- Modify: `TelegramBot.Worker/Services/CommandPreparer.cs:302-309`

**Interfaces:**
- Produces: `CommandCodes.Resave = "RESAVE"` and `CallbackPrefixes.Resave = "RESAVE:"`.
- Consumes: `CommandCatalog` and `CommandPreparer.IsRevitCommand`; no new Telegram callback handler is required because `CommandToggleHandler` derives supported prefixes from the catalogue.

- [ ] **Step 1: Establish the schema-validation baseline**

Run: `& .\scripts\validate-bim-schemas.ps1`

Expected: every TaskFile and ResultFile valid sample passes; every supplied invalid sample fails validation.

- [ ] **Step 2: Add the RESAVE command through existing data-driven seams**

```csharp
// CommandCodes.cs
public const string Resave = "RESAVE";

// CallbackPrefixes.cs
public const string Resave = "RESAVE:";

// CommandDefinition.cs
new(CommandCodes.Resave, "Resave RVT", CallbackPrefixes.Resave, CommandGroup.Automation),

// SlashCommandService.cs
CommandCodes.Ifc or CommandCodes.Resave => CommandPriorities.Medium,

// CommandPreparer.cs
or CommandCodes.Resave;
```

Add a `RESAVE` Worker command configuration matching PDF/DWG/NWC/DATA/IFC: `Revit.exe`, empty template, and `AllowedExtensions` equal to `[".rvt"]`.

- [ ] **Step 3: Build after the command-path changes**

Run: `dotnet build TelegramBot.slnx`

Expected: exit code 0. Confirm `/automation` can derive the new prefix through `CommandCatalog.All`, and `RESAVE` is classified as a Revit command so its arguments are `/language RUS` and its TaskFile path is supplied only in `REVITBIMFUSION_TASK_FILE`.

- [ ] **Step 4: Commit the completed command-path change**

```powershell
git add -- TelegramBot.Core/Constants/CommandCodes.cs TelegramBot.Core/Constants/CallbackPrefixes.cs TelegramBot.Server/Models/CommandDefinition.cs TelegramBot.Server/Services/Application/SlashCommandService.cs TelegramBot.Worker/appsettings.json TelegramBot.Worker/Services/CommandPreparer.cs
git commit -m 'feat: add RESAVE automation command'
```

### Task 2: Validate incoming ResultFile XML against the vendored XSD

**Files:**
- Create: `TelegramBot.Worker/Schemas/ResultFileValidator.cs`
- Modify: `TelegramBot.Worker/TelegramBot.Worker.csproj:28-31`
- Modify: `TelegramBot.Worker/Services/ResultAnalyzer.cs:1-70`

**Interfaces:**
- Produces: `ResultFileValidator.Validate(XmlReader reader)`, returning all XSD validation messages as `List<string>`.
- Consumes: `Docs/BimContract/ResultFile.schema.xsd` embedded as `TelegramBot.Worker.Schemas.ResultFile.schema.xsd`.
- Preserves: `ResultAnalyzer.TryReadResultFileAsync` return tuple and its `Invalid` result behavior.

- [ ] **Step 1: Confirm existing invalid ResultFile samples are rejected by the contract schema**

Run: `& .\scripts\validate-bim-schemas.ps1`

Expected: `resultfile-invalid-missing-status.xml` and `resultfile-invalid-bad-enum.xml` are reported as `CORRECTLY FAILED` before changing runtime code.

- [ ] **Step 2: Add a focused ResultFile schema validator**

```csharp
public static class ResultFileValidator
{
    private const string ManifestResourceName =
        "TelegramBot.Worker.Schemas.ResultFile.schema.xsd";

    private static readonly Lazy<XmlSchemaSet> Schemas =
        new(LoadSchemaSet, LazyThreadSafetyMode.ExecutionAndPublication);

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
            errors.Add($"{e.Severity}: {e.Message}");
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
```

Use the same lazy schema loading and validation-event collection pattern as `TaskFileValidator`; do not introduce a shared abstraction for two small validators.

```xml
<EmbeddedResource Include="$(BimContractDirectory)\ResultFile.schema.xsd"
                  LogicalName="TelegramBot.Worker.Schemas.ResultFile.schema.xsd" />
```

- [ ] **Step 3: Validate a fresh stream before deserialization**

```csharp
using (var validationStream = new FileStream(path, FileMode.Open, FileAccess.Read,
           FileShare.ReadWrite | FileShare.Delete))
using (var reader = XmlReader.Create(validationStream))
{
    var validationErrors = ResultFileValidator.Validate(reader);
    if (validationErrors.Count > 0)
    {
        RenameToBadFile(path);
        return (ResultFileReadStatus.Invalid, null,
            $"Plugin result file violates schema: {path}. {string.Join("; ", validationErrors)}");
    }
}

using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
    FileShare.ReadWrite | FileShare.Delete);
var result = (ResultFile)ResultFileSerializer.Deserialize(stream)!;
```

Extend the invalid-XML catch filter to `InvalidOperationException or XmlException`, so malformed XML from the validating reader follows the existing retry and `.bad` path. Keep I/O retries unchanged. Do not delete the ResultFile before a successful validation and deserialization.

- [ ] **Step 4: Run contract and compilation verification**

Run: `& .\scripts\validate-bim-schemas.ps1; dotnet build TelegramBot.slnx`

Expected: schema script succeeds and the full solution build exits with code 0.

- [ ] **Step 5: Commit the ResultFile validation change**

```powershell
git add -- TelegramBot.Worker/Schemas/ResultFileValidator.cs TelegramBot.Worker/TelegramBot.Worker.csproj TelegramBot.Worker/Services/ResultAnalyzer.cs
git commit -m 'fix: validate ResultFile against BIM contract'
```

### Task 3: Synchronize contract-version references and final verification

**Files:**
- Modify: `README.md:104-106`
- Modify: `AGENTS.md:12,129`
- Modify: `TelegramBot.Core/Models/TaskFile.cs:8-11`
- Modify: `TelegramBot.Core/Models/ResultFile.cs:32-35`

**Interfaces:**
- Produces: documentation references to canonical BIM contract version `2026-09-03`.
- Consumes: no runtime interfaces.

- [ ] **Step 1: Update stale version references only**

Replace every current TaskFile/ResultFile canonical-contract version reference in the listed files with `v2026-09-03`. Do not duplicate contract contents in this repository.

- [ ] **Step 2: Run final verification**

Run: `& .\scripts\validate-bim-schemas.ps1; dotnet build TelegramBot.slnx; git diff --check`

Expected: both schema groups pass, the solution builds with exit code 0, and `git diff --check` reports no whitespace errors.

- [ ] **Step 3: Inspect changed execution scope and commit documentation**

Run: `detect_changes({ scope: "all", repo: "TelegramBot" })`

Expected: only the command selection/Worker launch and ResultFile analysis flows are affected; no unrelated server or data flows are present.

```powershell
git add -- README.md AGENTS.md TelegramBot.Core/Models/TaskFile.cs TelegramBot.Core/Models/ResultFile.cs
git commit -m 'docs: update BIM contract version'
```
