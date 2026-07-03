# Repository Simplification Design

## Goal

Apply every accepted ponytail audit simplification while preserving observable bot and worker behavior. `DialogDismisser`, its Win32/PInvoke support, and the process monitoring that invokes it remain unchanged.

## Server

- Replace the slash-command strategy/context/result pipeline with one direct authorization flow and command dispatch.
- Make callback handlers return `Task`; dispatch results are currently ignored.
- Inject `TelegramOutputService` directly and delete its single-implementation interface.
- Remove `DataServices`; inject the existing data services directly.
- Make synchronous keyboard, filesystem, and update mapping APIs synchronous.
- Remove collection locks inside `UserSession`; `TelegramBotHostedService` already serializes all updates per user through `SessionManager.AcquireUserLockAsync`.
- Remove unused `SessionManager` logging paths and dead authorization fields.
- Replace the ASP.NET Web SDK and `Serilog.AspNetCore` with the generic Worker hosting stack.

## Worker

- Remove the command-slot semaphore; the drain gate and tracked running-task count already cap claims at `MaxConcurrentCommands`.
- Move session-completion checking into `ProcessRunner` and delete the one-method `SessionCompletionTracker`.
- Reduce `NavisworksPathResolver` to resolving the newest executable required by the Worker.
- Make synchronous Revit detection synchronous.
- Remove unused Worker data-service registrations and the unused `RevitInstallRoot` option.
- Keep `DialogDismisser`, its configuration, native helpers, and process-monitor loop intact.

## Shared Configuration and Startup

- Remove duplicated default command mappings from `WorkerOptions`; committed configuration remains the source of command definitions and startup validation rejects an empty mapping.
- Move host-specific database initialization and admin seeding into Server startup, allowing Data to drop Hosting and Binder package references.
- Remove the unused Hosting abstractions package from Core.
- Replace `ConcurrentQueue<DateTime>` inside the rate limiter's existing lock with `Queue<DateTime>`.

## Documentation

- Delete the two completed implementation plans under `Docs/superpowers/plans`.
- Update repository documentation only where removed types, options, or APIs are named.

## Boundaries

Do not change database schema, SQL semantics, Telegram command behavior, path validation, XML/XSD validation, retry classification, timeout handling, process shutdown, notification durability, or dialog dismissal.

## Verification

Project rules intentionally disable tests. The only verification command is:

```powershell
dotnet build TelegramBot.slnx
```

The build must finish with zero errors.
