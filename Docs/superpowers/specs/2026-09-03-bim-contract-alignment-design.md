# BIM contract alignment design

## Goal

Align TelegramBot with the RevitBIMFusion BIM-plugin contract version 2026-09-03 without changing the contract itself.

## Scope

- Add `RESAVE` as a Revit command exposed in the Telegram `/automation` group.
- Give `RESAVE` worker priority 3 (`Medium`).
- Run `RESAVE` through the existing Revit TaskFile/ResultFile handoff: `/language RUS` and the process-scoped `REVITBIMFUSION_TASK_FILE` environment variable.
- Validate every incoming ResultFile against the vendored canonical XSD before deserializing it. A schema-invalid file is treated as invalid, renamed to `.bad` where possible, and follows the existing retry policy.
- Embed the ResultFile XSD beside the existing TaskFile XSD and update stale references to the current contract version.

## Design

The existing command-selection flow is data-driven through `CommandCatalog`. Adding a `RESAVE` definition with `CommandGroup.Automation` and a dedicated callback prefix makes it available via `/automation` without new Telegram handler logic. The Worker configuration, command code, priority mapping, and `IsRevitCommand` are extended together so `RESAVE` uses the established Revit process path rather than a separate implementation.

`ResultFileValidator` mirrors `TaskFileValidator` and loads the embedded ResultFile XSD. `ResultAnalyzer.TryReadResultFileAsync` validates the opened XML stream before `XmlSerializer.Deserialize`; validation errors take the existing invalid-result path. Validation must read a fresh stream so deserialization still starts at the document root.

## Constraints

- Do not copy or modify the canonical contract or its schemas; only resync vendored schemas when the canonical source changes.
- Preserve current behavior for export commands and non-Revit wrapper commands.
- No new test project or tests, per repository policy. Verify using the existing BIM-schema script, full solution build, and focused source review.
