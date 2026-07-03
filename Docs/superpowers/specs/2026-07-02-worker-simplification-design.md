# Worker Simplification Design

> **Статус:** исторический proposal. Частично superseded реализацией от 2026-07-03. Текущие компоненты и DI описаны в `AGENTS.md`; этот файл не является operational source of truth.

## Goal

Make `TelegramBot.Worker` explicit and easy to follow while preserving its external behavior and BIM plugin contract.

## Boundaries

Keep:

- PostgreSQL `LISTEN/NOTIFY`, fallback polling, leases, and bounded concurrency.
- Task/Result XML formats, atomic task-file writes, unique attempt tokens, and path validation.
- Process timeout, retry classification, launch staggering, bounded stdout/stderr, and crash diagnostics.
- Completion outbox notification, inactive-session cleanup, and process termination during shutdown.

Remove:

- Disabled dialog dismissal and its Win32/PInvoke support.
- Process health sampling that only feeds logs and dialog dismissal.
- Unused Worker DI registrations and unused configuration.
- Single-method service wrappers and redundant concurrency primitives.
- Fake asynchronous APIs and hidden database writes in preparation code.
- Comments and documentation describing removed internals.

## Architecture

The execution path remains three explicit steps:

1. `CommandExecutionService` listens for work, claims only available slots, starts command tasks, and stops active work.
2. `CommandPreparer` validates input, resolves the executable, creates the process configuration, and manages XML exchange files. Preparation returns either a configuration or an error and does not update the database.
3. `ProcessRunner` owns process execution and all resulting database transitions: started, done, failed, retry, and session completion.

`SessionCleanupService` remains separate because it is an independent periodic retention job.

## Simplifications

### Worker host

- Register only services resolved by Worker.
- Remove dialog configuration, native logger initialization, and the separate BimLib log sink.
- Keep startup option validation and TaskDirectory creation.

### Queue orchestration

- Remove the command-slot semaphore. The drain gate and tracked-task count already prevent claims above `MaxConcurrentCommands`.
- Keep one lease cleanup loop instead of a generic background-loop framework.
- Move active-process shutdown into `ProcessRunner`, which owns the process collection.

### Command preparation

- Make Revit detection synchronous because OpenMcdf has no asynchronous API.
- Return preparation errors to `ProcessRunner`; do not mutate command status from `CommandPreparer`.
- Let task-file creation throw its original I/O exception instead of converting it to `false` and constructing a second exception.
- Keep security checks and copy shared command configuration before changing the executable path.

### Process execution

- Merge session-completion notification into a private `ProcessRunner` method and delete `SessionCompletionTracker`.
- Preserve result-file handling, output bounds, retries, timeout killing, and Revit journal evidence.
- Expose a single shutdown method instead of the active-process collection.

### BIM lookup

- Delete the unused `RevitInstallRoot` option.
- Reduce Navisworks lookup to the one operation Worker needs: resolve the newest available executable.
- Retain registry lookup and Revit-version caching because they avoid repeated file and registry work for command batches.

## Error Handling

Trust-boundary validation, atomic file exchange, retry classification, kill timeouts, and best-effort cleanup remain. Simplification must not turn invalid input into a runnable process or allow shutdown to orphan Revit/Navisworks processes.

## Verification

Project rules intentionally disable tests. Verification is:

```powershell
dotnet build TelegramBot.slnx
```

The build must finish with zero errors. Documentation and configuration references to removed Worker components must also be absent.
