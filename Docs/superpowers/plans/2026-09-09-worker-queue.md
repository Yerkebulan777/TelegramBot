# Worker queue stability implementation plan

**Goal:** Execute queued work through one polling loop and make persistence failures visible.

**Approved design:** PostgreSQL remains the source of truth. Poll every 10 seconds; remove Worker LISTEN and delayed drain callbacks. Preserve SQL claim, concurrency, partition locks and crash leases. Retry result persistence separately from process execution. Explain duplicate submissions using existing command/session metadata.

**Constraints:** No added tests (AGENTS.md). Full solution build required. Do not restart deployed services or modify production queue. No new scheduler, broker, or schema migration.

- [x] Simplify CommandExecutionService and CommandOrchestrator: immediate initial poll, then a cancellable delay; cleanup before claim; process monitoring independent. Remove notification and retry timer paths.
- [x] Replace silently swallowed final status writes with bounded transient retries and an explicit persistence exception. Do not classify that exception as a Revit failure. Keep result evidence when persistence fails.
- [x] Return active conflict metadata from SessionDataService, render it for partial and complete duplicate submissions, and list only newly queued files as added.
- [x] Update configuration and README/ExecutionAlgorithm/agent documentation. Preserve crash recovery limitations explicitly.
- [x] Build TelegramBot.slnx, inspect the complete diff and GitNexus affected scope, verify removed scheduling paths have no callers.

**Validation cases for an isolated runtime:** pending work picked up without NOTIFY; future retries wait for NextRetryAt; lost PostgreSQL connection retries after a delay; result-write outage cannot be interpreted as process failure; duplicate submission reports existing session/status/time; cancellation exits polling without tight loops. Live fault injection is not authorized against the running bot.

## Verification results

- Full solution build: 0 warnings, 0 errors.
- Invoked the compiled formatter with 50 long queued names and 22 conflicts: 2947 characters; all-skipped message: 1689 characters. Existing session, status, UTC timestamp and truncation verified.
- Invoked the compiled status-write method against 127.0.0.1:1 (isolated unavailable endpoint): CommandPersistenceException after 18.1 seconds; retry delays applied, no false success.
- GitNexus scope: queue, result handling and submission formatting; risk high as expected. No new test files or live queue mutations.
- End-to-end Revit execution, live SQL conflict retrieval and recovery of the previously reported 11 rows were not validated in production.
