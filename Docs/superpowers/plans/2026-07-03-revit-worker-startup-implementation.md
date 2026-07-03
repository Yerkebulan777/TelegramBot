# Revit Worker Startup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make TelegramBot launch Revit tasks through `REVITBIMFUSION_TASK_FILE` and expose `DATA` end to end.

**Architecture:** TelegramBot creates the existing TaskFile, starts Revit without `/command`, and sets a child-process environment variable. RevitBIMFusion validates that path during startup and invokes the existing worker handler once from `Idling`.

**Tech Stack:** .NET 10 Worker, Revit API 2019/2023/2026, PostgreSQL-backed Telegram bot

---

### Task 1: Add DATA to TelegramBot

**Files:**
- Modify: `TelegramBot.Core/Constants/CommandCodes.cs`
- Modify: `TelegramBot.Core/Constants/CallbackPrefixes.cs`
- Modify: `TelegramBot.Server/Models/CommandDefinition.cs`
- Modify: `TelegramBot.Server/Services/Application/SlashCommandService.cs`
- Modify: `TelegramBot.Core/Config/WorkerOptions.cs`
- Modify: `TelegramBot.Worker/appsettings.json`

- [x] Add `DATA`, its callback, export button, medium priority, and Revit command configuration accepting `.rvt`.
- [x] Verify `rg -n '"DATA"|CommandCodes.Data|CallbackPrefixes.Data'` reaches every layer.

### Task 2: Replace TelegramBot Revit CLI dispatch

**Files:**
- Modify: `TelegramBot.Core/Config/WorkerOptions.cs`
- Modify: `TelegramBot.Worker/Services/CommandPreparer.cs`
- Modify: `TelegramBot.Worker/Services/ProcessRunner.cs`
- Modify: `TelegramBot.Worker/Services/ErrorClassifier.cs`
- Modify: `TelegramBot.Worker/Program.cs`

- [x] Replace `RevitDispatcherCommand` with the environment-variable constant.
- [x] Use empty Revit argument templates and allow empty configured arguments.
- [x] Set the absolute TaskFile path in `ProcessStartInfo.Environment`.
- [x] Treat a missing Revit ResultFile as failure even when the process exits with code zero.
- [x] Classify unsupported commands as permanent.

### Task 3: Activate the Revit bridge from Idling

**Files:**
- Modify: `C:/Users/y.zhumabayev/Repository/RevitBIMFusion/WorkerBridge/Services/TaskFilePathResolver.cs`
- Modify: `C:/Users/y.zhumabayev/Repository/RevitBIMFusion/WorkerBridge/Commands/WorkerCommandHandler.cs`
- Modify: `C:/Users/y.zhumabayev/Repository/RevitBIMFusion/RevitBIMFusion/Application.cs`

- [x] Resolve and validate `REVITBIMFUSION_TASK_FILE`.
- [x] Add a handler entry point accepting `UIApplication`, task path, and headless mode.
- [x] Subscribe a one-shot `Idling` callback during startup and execute through the existing handler registry.
- [x] Preserve manual ribbon/file-picker behavior.

### Task 4: Synchronize guidance

**Files:**
- Modify: `README.md`
- Modify: `AGENTS.md`
- Modify: `Docs/RevitCrashes.md`
- Modify: `C:/Users/y.zhumabayev/Repository/RevitBIMFusion/WorkerBridge/AGENTS.md`
- Modify: `C:/Users/y.zhumabayev/Repository/RevitBIMFusion/RevitBIMFusion/AGENTS.md`

- [x] Remove active `/command "WORKER"` instructions.
- [x] Document DATA and the environment-variable/Idling flow.

### Task 5: Verify

- [x] Run `dotnet build TelegramBot.slnx`.
- [x] Run RevitBIMFusion builds for `Debug.R19`, `Debug.R23`, and `Debug.R26`.
- [x] Confirm no active Revit `/command` templates remain.
- [x] Review both git diffs without modifying unrelated user changes.
