# Worker State-Machine Hardening Design

## Goal

Prevent a pre-launch Revit coordination failure from reserving a command for the full process lease, and consolidate the Worker command lifecycle behind explicit outcomes and atomic persistence.

## Decisions

- A lease remains the crash-recovery mechanism for a running process. It is not removed.
- The Revit advisory-lock wait is bounded by the command cancellation token, not Npgsql's default 30-second command timeout.
- A failure before `Process.Start()` is a retryable launch failure. It is distinct from an uncertain persistence failure after process evidence may exist.
- Every terminal command transition updates the command, determines session completion, and writes the completion outbox event in one database transaction protected by a session advisory xact lock.
- Command preparation returns an explicit success/failure value; it does not write command status as a hidden side effect.
- Command traits shared by Server and Worker (priority and Revit execution) live in Core. Telegram labels and callback prefixes remain Server concerns.

## Modules

`RevitLaunchGate` serializes only actual Revit starts. It throws `RevitLaunchException` for failures before `Process.Start()`.

`CommandAttemptPersistence` is represented by focused methods on `CommandDataService`: terminal completion is atomic, while retry remains a separate non-terminal transition.

`CommandPreparer` becomes a resolver and launch-spec builder. `CommandTaskFileStore` owns task/result names, contract serialization, validation, and cleanup.

`SessionStatusView` owns shared status rendering. Three callback handlers own status browsing, deletion, and rerun respectively.

## Verification

The repository prohibits adding tests. Verification therefore uses a deterministic PostgreSQL advisory-lock smoke harness outside the repository, the full solution build, `dotnet format --verify-no-changes`, and GitNexus change-impact analysis.
