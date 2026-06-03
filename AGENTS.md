# AGENTS.md

Guidance for agentic coding agents working in this repository.

## Project Overview

Telegram bot using long-polling, split into 3 projects. No webhooks, no MVC controllers. All services are **Singletons**.

```
TelegramBot.Core   ←──  TelegramBot.Data
       ↑                       ↑
       └──── TelegramBot.Server ──┘
```

- **TelegramBot.Core** — Models, DTOs, interfaces, config, constants. Zero Telegram SDK dependency.
- **TelegramBot.Data** — SQLite persistence via Dapper. References Core only. SQL constants in `Sql/` (5 partial files).
- **TelegramBot.Server** — Telegram infrastructure, application services, handlers, hosting, helpers. References Core + Data.

---

## Build & Run Commands

```bash
# Build all projects (use this to verify changes)
dotnet build TelegramBot.slnx

# Run the server
dotnet run --project TelegramBot.Server/TelegramBot.Server.csproj

# Release publish
dotnet publish TelegramBot.Server/TelegramBot.Server.csproj -c Release

# Format code (.editorconfig exists with naming rules, see Known Issues)
dotnet format TelegramBot.slnx
```

**Tests are intentionally disabled for this project.** Do not add test projects, do not add unit/integration tests, and do not run `dotnet test`. After making changes, verify correctness by building successfully with `dotnet build TelegramBot.slnx`.

---

## Configuration

- `TelegramBot.Server/appsettings.json` — committed, contains Serilog config and `FileSystem` options
- `TelegramBot.Server/appsettings.Local.json` — **gitignored**, put secrets here (bot token, local overrides)
- Required config keys:
  - `TelegramBot:Token` — bot token (also settable via env var `TelegramBot__Token`)
  - `TelegramBot:AdminUserIds` — long[] of admin Telegram IDs (also settable via `TelegramBot__AdminUserIds__0`, `__1`, etc.)
  - `FileSystem:RootPath` — filesystem browser root (validated on startup via `FileSystemOptions`)
  - `ConnectionStrings:Sqlite` — SQLite connection string (defaults to `"Data Source=botdata.db"`)

---

## Architecture & Request Flow

```
Telegram API -> TelegramBotHostedService (polling)
             -> TelegramUpdateMapper (Update -> MessageDto | CallbackQueryDto)
             -> CommandAppService.HandleUserCommandAsync (text commands)
                ├── /start bypasses access check → registration or help
                └── other commands → BotUsers.Status must be Approved
             -> ICallbackDispatcher -> CallbackDispatcher.DispatchAsync (inline keyboard callbacks)
                ├── REQACCESS/APPROVEUSER/REJECTUSER bypass access check
                └── all other callbacks → user must be Approved
```

DI is wired in `TelegramBot.Server/Extensions/DependencyInjectionExtensions.cs`. The filesystem root comes from `FileSystemOptions` (bound to `"FileSystem"` config section).

### Callback Handling — Chain of Responsibility

`CallbackDispatcher` (implements `ICallbackDispatcher`) routes callbacks to the first `ICallbackHandler` that `CanHandle()` the prefix (sorted by `Priority`, lower = first). All handlers extend `CallbackHandlerBase`.

Handler hierarchy: `AccessRequestHandler` (Priority 0) > `FileNavigationHandler` (10) > `FileSelectionHandler` (20) > `ExportCommandHandler`, `AutomationCommandHandler`, `SessionManagementHandler`, `CommandSelectionHandler` (100).

Callback prefixes are constants in `CallbackPrefixes` (`TelegramBot.Core/Models/CallbackPrefixes.cs`). Command codes in `TelegramBot.Core/Constants/CommandCodes.cs`. Use `CallbackDataParser.Parse(data)` to get a `ParsedCallback`, then match with `parsed.Is(CallbackPrefixes.GoToParent)`. For Markdown escaping, use `MarkdownHelper` from `TelegramBot.Server/Helpers/`.

### Database

Tables: `BotUsers`, `Sessions`, `Commands`, `TrackedMessages` (composite PK). Soft-delete only — set `Status = 'Deleted'`, never `DELETE FROM`.
DB file: `botdata.db`. Initialized at startup via `host.InitializeDatabaseAsync()` + `host.SeedAdminUsersAsync()`.
All data access uses **Dapper** (`TelegramBot.Data/SqliteDataService.cs`). SQL constants in `TelegramBot.Data/Sql/` (5 partial files per entity).

---

## Code Style Guidelines

### C# Language Features

- **Target framework**: .NET 10 (`net10.0`)
- **Nullable reference types**: enabled — always annotate nullability (`string?`, `T?`)
- **Implicit usings**: enabled — do not add `using System;` etc. unless needed beyond the implicit set
- **File-scoped namespaces** required: `namespace TelegramBot.Core.Models;`
- **Primary constructors** (C# 12) are used in newer services; either style is acceptable but be consistent within a file

### Naming Conventions

| Element | Convention | Example |
|---|---|---|
| Classes | `PascalCase` | `CommandAppService` |
| Interfaces | `I` + `PascalCase` | `IDataService` |
| Methods | `PascalCase` | `HandleCallbackAsync` |
| Async methods | suffix `Async` | `InitializeDatabaseAsync` |
| Private fields | `_camelCase` | `_logger`, `_sessionManager` |
| Properties | `PascalCase` | `SelectedFiles`, `CurrentPath` |
| Local variables | `camelCase` | `chatId`, `sessionId` |
| Parameters | `camelCase` | `userId`, `cancellationToken` |

### Namespace Conventions

Namespaces must match folder structure:
- `TelegramBot.Core.Models`, `TelegramBot.Core.DTOs`, `TelegramBot.Core.Interfaces`, `TelegramBot.Core.Config`, `TelegramBot.Core.Constants`
- `TelegramBot.Data`
- `TelegramBot.Server.Services.Application`, `TelegramBot.Server.Services.Infrastructure.Telegram`, `TelegramBot.Server.Helpers`

### Imports / Using Directives

- Place `using` directives at the top of the file, before the namespace
- Order: framework namespaces, then third-party (`Dapper`, `Serilog`, `Telegram.Bot`), then project-internal (`TelegramBot.*`)
- Do not add unnecessary usings

### Dependency Injection

- Register all new services as **Singletons** in `DependencyInjectionExtensions.cs`
- Use `_ = services.AddSingleton<IFoo, Foo>()` (discard the fluent return value)
- Inject dependencies via constructor; store in `readonly` private `_camelCase` fields
- New callback handlers: implement `ICallbackHandler`, extend `CallbackHandlerBase`, register in `AddCallbackHandlers()`

### Async / Await

- All async methods return `Task` or `Task<T>` — never `async void`
- Always suffix async methods with `Async`
- Do **not** use `ConfigureAwait(false)` — this is an application, not a library
- `CancellationToken` is threaded from `BackgroundService.ExecuteAsync`; inner methods generally do not require it unless doing I/O loops

### Error Handling

- Startup: wrapped in `try/catch` with `Log.Fatal` in `Program.cs` — do not remove
- Telegram API calls: catch `ApiRequestException` specifically, log as `LogWarning`, let the bot continue
- `TelegramOutputService` has retry logic for HTTP 429 (rate limiting) via `ExecuteWithRetryAsync`
- Do not swallow unknown exceptions — log at `LogError` or rethrow
- Avoid empty `catch` blocks
- Handler base class catches `OperationCanceledException` (logs + rethrows) and general `Exception` (logs at Error + rethrows)

### Logging

- Use `ILogger<T>` injected via constructor (Serilog backs it)
- Use structured logging with message templates — **not** string interpolation:
  ```csharp
  _logger.LogInformation("Received command '{Command}' from {UserId}", command, userId);
  ```
- Log levels: `LogDebug` for diagnostics, `LogInformation` for normal flow, `LogWarning` for recoverable issues, `LogError` / `Log.Fatal` for failures

### Collections & Thread Safety

- `UserSession` uses fine-grained locks (`_commandLock`, `_selectionLock`, `_navigationLock`, `_messageLock`) — follow this pattern for new mutable state
- `PathMap` uses `ConcurrentDictionary<string, string>`
- For new shared dictionaries, prefer `ConcurrentDictionary<,>`

### SQL / Data Access (TelegramBot.Data)

- Use `await using var conn = new SqliteConnection(...)` — open a fresh connection per method
- Use **Dapper** for all queries (no raw `SqliteCommand`/`SqliteDataReader`)
- SQL statements go in verbatim string literals (`@"..."`)
- Use parameterized queries — never string-concatenate user input into SQL
- Soft-delete only: `SET Status = 'Deleted'`, never `DELETE FROM`
- For transactions, use `conn.BeginTransactionAsync()`

### Telegram Messages

- Plain messages: `ParseMode.MarkdownV2` — escape special characters with `MarkdownHelper.EscapeMarkdownV2()`
- Messages with inline keyboards: `ParseMode.Markdown` — escape with `MarkdownHelper.EscapeMarkdown()`
- Do not mix the two parse modes
- All Telegram API methods must be current — do not use deprecated approaches

### General

- XML doc comments (`/// <summary>`) on new interface methods
- Use `required` keyword on model properties that must always be set
- Prefer `??` and `?? throw new InvalidOperationException(...)` over unchecked null dereferences
- Maintain good code readability and unify methods for easier editing

---

## Known Issues (Do Not Worsen)

- `.editorconfig` exists with naming rules, formatting preferences, and `generated_code = true` markers for data service and handlers — `dotnet format` respects these
- No CI/CD pipeline or automated tests — the only verification is a successful `dotnet build`
- Keep secrets out of committed config files — use `TelegramBot.Server/appsettings.Local.json` (gitignored) or env var `TelegramBot__Token`; never hardcode tokens

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **TelegramBot** (824 symbols, 2228 relationships, 68 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

> If any GitNexus tool warns the index is stale, run `npx gitnexus analyze` in terminal first.

## Always Do

- **MUST run impact analysis before editing any symbol.** Before modifying a function, class, or method, run `gitnexus_impact({target: "symbolName", direction: "upstream"})` and report the blast radius (direct callers, affected processes, risk level) to the user.
- **MUST run `gitnexus_detect_changes()` before committing** to verify your changes only affect expected symbols and execution flows.
- **MUST warn the user** if impact analysis returns HIGH or CRITICAL risk before proceeding with edits.
- When exploring unfamiliar code, use `gitnexus_query({query: "concept"})` to find execution flows instead of grepping. It returns process-grouped results ranked by relevance.
- When you need full context on a specific symbol — callers, callees, which execution flows it participates in — use `gitnexus_context({name: "symbolName"})`.

## Never Do

- NEVER edit a function, class, or method without first running `gitnexus_impact` on it.
- NEVER ignore HIGH or CRITICAL risk warnings from impact analysis.
- NEVER rename symbols with find-and-replace — use `gitnexus_rename` which understands the call graph.
- NEVER commit changes without running `gitnexus_detect_changes()` to check affected scope.

## Resources

| Resource | Use for |
|----------|---------|
| `gitnexus://repo/TelegramBot/context` | Codebase overview, check index freshness |
| `gitnexus://repo/TelegramBot/clusters` | All functional areas |
| `gitnexus://repo/TelegramBot/processes` | All execution flows |
| `gitnexus://repo/TelegramBot/process/{name}` | Step-by-step execution trace |

## CLI

| Task | Read this skill file |
|------|---------------------|
| Understand architecture / "How does X work?" | `.claude/skills/gitnexus/gitnexus-exploring/SKILL.md` |
| Blast radius / "What breaks if I change X?" | `.claude/skills/gitnexus/gitnexus-impact-analysis/SKILL.md` |
| Trace bugs / "Why is X failing?" | `.claude/skills/gitnexus/gitnexus-debugging/SKILL.md` |
| Rename / extract / split / refactor | `.claude/skills/gitnexus/gitnexus-refactoring/SKILL.md` |
| Tools, resources, schema reference | `.claude/skills/gitnexus/gitnexus-guide/SKILL.md` |
| Index, status, clean, wiki CLI commands | `.claude/skills/gitnexus/gitnexus-cli/SKILL.md` |

<!-- gitnexus:end -->
