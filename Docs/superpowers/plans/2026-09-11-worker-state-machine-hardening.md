# Worker State-Machine Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Revit launch coordination retry safely and simplify command/session state transitions without changing command behavior.

**Architecture:** Keep the lease for crash recovery, distinguish pre-start launch failures from persistence failures, and move terminal command completion plus outbox creation into one SQL operation. Preparation returns a typed result and task-file lifecycle becomes a focused module; shared command traits move to Core and callback workflows are split by responsibility.

**Tech Stack:** C# 12, .NET 10, PostgreSQL/Npgsql, Dapper, Telegram.Bot.

**Spec:** `Docs/superpowers/specs/2026-09-11-worker-state-machine-design.md`

## Global Constraints

- Do not add tests; `AGENTS.md` requires a full solution build after changes.
- Preserve PostgreSQL parameterization, soft-delete invariants, and ResultFile evidence on persistence failure.
- Keep all Revit task/result XML compatible with the canonical BIM contract.

---

### Task 1: Harden pre-launch Revit coordination

**Files:**
- Modify: `TelegramBot.Worker/Services/RevitLaunchGate.cs`
- Create: `TelegramBot.Worker/Services/RevitLaunchException.cs`
- Modify: `TelegramBot.Worker/Services/ProcessRunner.cs`

**Interfaces:**
- Produces: `RevitLaunchException`, a retryable exception meaning no external process was started.
- Consumes: existing `CommandDataService.ScheduleRetryAsync` through `ProcessRunner.HandleFailureAsync`.

- [x] Make only advisory-lock acquisition use an unbounded Npgsql command timeout and the supplied cancellation token.
- [x] Wrap only failures before `Process.Start()` in `RevitLaunchException`.
- [x] Preserve `CommandPersistenceException` solely for uncertain database writes after process/result evidence exists.
- [ ] Verify with a PostgreSQL advisory-lock harness that a waiter survives more than 30 seconds until cancellation or lock release.

### Task 2: Make terminal completion atomic

**Files:**
- Modify: `TelegramBot.Data/Sql/Queries.Commands.cs`
- Modify: `TelegramBot.Data/CommandDataService.cs`
- Modify: `TelegramBot.Worker/Services/ProcessRunner.cs`

**Interfaces:**
- Produces: `CompleteCommandAndNotifyAsync(PendingCommand, string, int?, string?)`.
- Consumes: `PendingCommand` correlation/session identifiers and terminal `Statuses.Done` or `Statuses.Failed`.

- [x] Add a parameterized transaction that writes a terminal command status, detects the final command, marks the session, inserts an outbox row, and emits `pg_notify` atomically.
- [x] Replace every terminal `UpdateCommandStatusAsync` plus completion-notification pair in `ProcessRunner` with this method.
- [x] Keep retry transitions non-terminal and preserve their retry-count semantics.

### Task 3: Deepen preparation and command metadata modules

**Files:**
- Create: `TelegramBot.Core/Models/CommandTraits.cs`
- Create: `TelegramBot.Worker/Services/CommandTaskFileStore.cs`
- Modify: `TelegramBot.Worker/Services/CommandPreparer.cs`
- Modify: `TelegramBot.Worker/Services/ProcessStarter.cs`
- Modify: `TelegramBot.Worker/Services/ProcessRunner.cs`
- Modify: `TelegramBot.Worker/Services/ResultAnalyzer.cs`
- Modify: `TelegramBot.Server/Services/Application/SlashCommandService.cs`

**Interfaces:**
- Produces: `CommandTraits.GetPriority` and `CommandTraits.RequiresRevit`; `CommandPreparationResult`; `CommandTaskFileStore` task/result operations.
- Consumes: Core command codes, Worker options, and existing XML contract validators.

- [x] Move command priority and Revit classification to Core traits.
- [x] Return preparation success/failure explicitly and make `ProcessRunner` own the terminal Failed transition.
- [x] Move task file paths, serialization/validation, and cleanup to `CommandTaskFileStore`.

### Task 4: Split session callback workflows and align temporary cleanup diagnostics

**Files:**
- Create: `TelegramBot.Server/Services/Application/Handlers/SessionStatusView.cs`
- Create: `TelegramBot.Server/Services/Application/Handlers/SessionStatusHandler.cs`
- Create: `TelegramBot.Server/Services/Application/Handlers/SessionDeletionHandler.cs`
- Create: `TelegramBot.Server/Services/Application/Handlers/CommandRerunHandler.cs`
- Modify: `TelegramBot.Server/Services/Application/Handlers/SessionManagementHandler.cs`
- Modify: `TelegramBot.Server/Extensions/DependencyInjectionExtensions.cs`
- Modify: `TelegramBot.Worker/Services/RevitTemporaryDirectoryCleaner.cs`
- Modify: `Docs/ExecutionAlgorithm.md`

**Interfaces:**
- Produces: one callback handler per status, deletion, and rerun workflow; `SessionStatusView` owns shared rendering.
- Consumes: existing callback prefixes and callback dispatcher registration.

- [ ] Move each prefix into exactly one handler and derive each handler's prefix set from its dispatch table.
- [ ] Keep callback payloads and rendered Telegram output unchanged.
- [x] Log rejected temporary directory paths with contract-diagnostic fields while preserving the deletion validation.

### Task 5: Verify and document

**Files:**
- Modify: `README.md`
- Modify: `Docs/ExecutionAlgorithm.md`

- [x] Update retry, lease, and launch-gate documentation.
- [x] Run `dotnet format --verify-no-changes` and `dotnet build TelegramBot.slnx`.
- [x] Run GitNexus `detect-changes --scope all --repo .` and inspect the affected Worker flows.
