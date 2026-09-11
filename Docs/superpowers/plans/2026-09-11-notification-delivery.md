# Notification delivery and message lifecycle

**Goal:** Recover delivery after transient failures and worker crashes; remove stale interface messages promptly without deleting fresh completion results.

**Approved design:** Two independent supervised loops backed by PostgreSQL: notification delivery and message deletion. Remove notification LISTEN/NOTIFY and in-memory channel. Keep atomic completion and outbox insertion, extend durable delivery to starts, recover missing completion events. Store delivery acknowledgement and message tracking together. Explicit message kinds and deletion/retry deadlines replace clearing all chat history.

**Constraints:** C# / net10.0; parameterized Dapper SQL; additive idempotent migration; only TrackedMessages permits physical deletion; preserve public behavior except the approved lifecycle changes. No new tests per AGENTS.md. No deployment or commit.

- [x] Delivery: update NotificationSenderService and NotificationOutboxDataService to one polling loop, classify Telegram failures, retry acknowledgement without re-sending an already acknowledged message, use cancellation. Remove obsolete listener/channel/model and update DI.
- [x] Persistence: add event metadata and unique start-event index; save completion tracking with sent acknowledgement in one transaction. Repair missing completion events. Ensure lease recovery uses session completion locks and creates completion events atomically. Starts are durable and ordered before completion.
- [x] Cleanup: add Kind/DeleteAfter/NextDeleteAttemptAt and message identity uniqueness. Record deletion intent, protect completion results, retry due deletions fairly, retain batch deletion, expose tracking errors and retry persistence.
- [x] Documentation: update README, ExecutionAlgorithm, AGENTS and CLAUDE to describe the two loops, retention and recovery semantics.
- [x] Verification: full solution build, focused independent review, git diff --check and GitNexus change analysis. Runtime PostgreSQL/Telegram fault scenarios require a configured isolated environment; report any unavailable verification honestly.

Verification outcome: full solution build passed with 0 warnings and 0 errors; diff whitespace check passed; independent SQL/lifecycle review completed and legacy migration finding corrected. GitNexus change analysis reports critical impact across 26 indexed flows; flow enumeration has tool coverage limits and is not proof of runtime safety. PostgreSQL/Telegram fault injection was not executed: no isolated PostgreSQL runtime was available. Changes are uncommitted on codex/reliable-notifications.

## Strict quality review follow-up

- Canonical completion SQL replaces the duplicate count query and conditional; obsolete NOTIFY payload parameters and an unused public completion method removed.
- Deletion acknowledgement and retry deadlines now persist atomically through SaveDeletionProgressAsync.
- Tracking requires explicit message date/deadline; unused overload and hardcoded data-layer retention fallback removed.
- Shared connection factory owns cancellation and failed-open disposal; notification/tracking copies removed.
- Claim returns one nullable item, matching SQL LIMIT 1.
- CompletionMessageFormatter owns pure text rendering; sender handles delivery. No changed file exceeds 1000 lines (largest: 490).

Validation: final full solution build passed with 0 errors and 0 warnings; independent follow-up review found no actionable regressions; whitespace check passed. Runtime PostgreSQL/Telegram failure scenarios remain unexecuted. Telegram 429 waits asynchronously while retaining the shared sender lock; healthy replicas cannot bypass the cooldown. Shutdown or connection loss can release that lock before the cooldown ends; NextAttemptAt still protects the failed item.
