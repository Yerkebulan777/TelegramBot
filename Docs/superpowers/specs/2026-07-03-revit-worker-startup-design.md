# Revit Worker Startup Design

> **Статус:** реализовано. Актуальный handoff закреплён только в canonical `RevitBIMFusion/Docs/BimPluginContract.md`; incident history — в `Docs/RevitCrashes.md`.

## Goal

Replace the unsupported `Revit.exe /command "WORKER" ...` launch with a supported
process-scoped handoff, and expose the existing RevitBIMFusion `DATA` export through
TelegramBot end to end.

## Root Cause

Revit does not dispatch external commands from the `/command` process argument.
Production journals show `WORKER` being treated as a file name, so
`WorkerCommand.Execute` and `TaskFilePathResolver` are never reached.

## Launch Contract

TelegramBot.Worker creates TaskFile as before, then launches `Revit.exe` without
custom command arguments. It places the absolute TaskFile path in the child process
environment variable:

```text
REVITBIMFUSION_TASK_FILE=C:\...\task_project_42.xml
```

The variable is scoped to that `ProcessStartInfo`, so parallel Revit processes do
not share task state.

RevitBIMFusion reads the variable during `Application.OnStartup`. When present and
valid, it subscribes a one-shot handler to Revit's `Idling` event. The handler
unsubscribes before execution, obtains the `UIApplication`, and invokes the existing
Worker bridge with the supplied TaskFile path. Task parsing, export, ResultFile
writing, cleanup, and process close remain owned by the existing bridge.

Manual ribbon execution remains available and keeps its file-picker fallback.

## DATA Command

TelegramBot adds `DATA` to the existing command catalog:

- `CommandCodes` and callback prefix;
- export command button and priority;
- Worker default options and `appsettings.json`;
- Revit executable resolution;
- TaskFile documentation.

`DATA` uses the same startup contract as PDF/DWG/NWC. Its TaskFile contains
`<commandText>DATA</commandText>`, which RevitBIMFusion already maps to
`ExportType.Data`.

## Failure Handling

- Missing/invalid environment path: log and do not schedule automatic execution.
- Task parse or export failure: write the existing failed ResultFile when its
  destination is known.
- Startup initialization failure: preserve the existing startup failure behavior.
- Automatic execution is one-shot; it cannot run twice from repeated `Idling`
  events.
- TelegramBot treats `NotImplemented:` and `Unsupported command:` as permanent
  failures.

## Documentation

Update both copies of `BimPluginContract.md`, TelegramBot `README.md`/`AGENTS.md`,
RevitBIMFusion worker guidance, and `RevitCrashes.md`. Remove all claims that
`/command "WORKER"` can activate an AddIn command.

## Verification

Repository policy forbids adding or running tests in TelegramBot. Verification is:

1. Build `TelegramBot.slnx`.
2. Build the supported RevitBIMFusion configurations available locally.
3. Inspect generated `ProcessStartInfo`: no `/command`, absolute environment path.
4. Confirm both contract documents describe the same environment-variable flow.
5. Confirm `DATA` exists across Telegram command catalog and Worker configuration.
