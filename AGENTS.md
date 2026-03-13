# AGENTS.md

Guidance for agentic coding agents working in this repository.

## Project Overview

Single .NET 8 background service (`TelegramBotServer`) — a Telegram bot using long-polling.
No webhooks, no MVC controllers, no test projects.

---

## Build & Run Commands

```bash
# Build
dotnet build TelegramBotServer/TelegramBotServer.csproj

# Run (requires appsettings.Local.json with bot token, or env var TelegramBot__Token)
dotnet run --project TelegramBotServer/TelegramBotServer.csproj

# Release publish
dotnet publish TelegramBotServer/TelegramBotServer.csproj -c Release
```

**There are no automated tests.** There is no test project and no test runner command.
After making changes, verify correctness by building successfully (`dotnet build`).

---

## Configuration

- `appsettings.json` — committed, contains Serilog config and empty bot token
- `appsettings.Local.json` — **gitignored**, contains secrets (bot token, local overrides)
- `appsettings.Development.json` — environment-specific overrides
- Required config keys:
  - `TelegramBot:Token` — Telegram bot token (set in `appsettings.Local.json` or env var)
  - `ConnectionStrings:Sqlite` — SQLite path (defaults to `"botdata.db"`)

---

## Architecture & Request Flow

```
Telegram API → TelegramBotHostedService (polling)
             → TelegramUpdateMapper (Update → MessageDto | CallbackQueryDto)
             → Authorization check (AuthService + SessionManager)
             → CommandAppService.HandleUserCommandAsync / HandleCallbackAsync
```

All services are **Singletons**. DI is wired in `DependencyInjectionExtensions.cs`.
The filesystem root is hardcoded as `"B:\\"` in `CommandAppService`.

### Key Services

| Class | Responsibility |
|---|---|
| `TelegramBotHostedService` | Entry point, polling loop, `/auth` flow |
| `CommandAppService` | Central command/callback dispatcher (~970 lines, `partial`) |
| `FileSystemBrowser` | Builds inline keyboards for filesystem navigation (`partial`) |
| `KeyboardBuilder` | Context-aware keyboards with selection state |
| `SessionManager` | In-memory sessions (`ConcurrentDictionary`, 5-min timeout) |
| `TelegramOutputService` | Send/edit Telegram messages |
| `SqliteDataService` | All DB persistence via `Microsoft.Data.Sqlite` + Dapper |
| `AuthService` | Whitelist + password auth (plain-text, default `qwerty123`) |

### Callback Data Protocol

Inline keyboard buttons use short tokens (≤64 bytes Telegram limit).
Full paths stored in `UserSession.PathMap[token]`.

Prefixes: `NAV1:`, `NAV2:`, `FILE:`, `SELMODE:`, `APPLYFILES:`, `CANCELSEL:`,
`CANCELFILESEL:`, `PDF:`, `DWG:`, `NWC:`, `IFC:`, `BIMDOC:`, `CLASHREP:`,
`AUTORES:`, `APPLYCOMMANDS:`, `CANCELCOMMANDSSEL:`, `Sessiondetails:`,
`Deletesession:`, `Deletecommand:`, `Backtostatus:`

### Database

Tables: `Sessions`, `Commands`, `Whitelist`, `Credentials`.
Soft-delete only — rows are never physically removed (status = `"Deleted"`).
DB file: `botdata.db` in the project directory.

---

## Code Style Guidelines

### C# Language Features

- **Target framework**: .NET 8 (`net8.0`)
- **Nullable reference types**: enabled — always annotate nullability (`string?`, `T?`)
- **Implicit usings**: enabled — do not add `using System;`, `using System.Collections.Generic;`, etc. unless needed beyond the implicit set
- **File-scoped namespaces** are preferred: `namespace TelegramBotServer.Services;`
- **Primary constructors** (C# 12) are used in some services; either style is acceptable but be consistent within a file

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

> Note: Some existing `UserSession` properties (`statusLevel`, `sessionId`) violate the property convention — do not perpetuate this; use `PascalCase` for new properties.

### Imports / Using Directives

- Place `using` directives at the top of the file, before the namespace declaration
- Order: framework namespaces first, then third-party (`Dapper`, `Serilog`, `Telegram.Bot`), then project-internal (`TelegramBotServer.*`)
- Do not add unnecessary usings

### Dependency Injection

- Register all new services as **Singletons** in `DependencyInjectionExtensions.cs`
- Use `_ = services.AddSingleton<IFoo, Foo>()` (discard the fluent return value explicitly)
- Inject dependencies via constructor; store in `readonly` private `_camelCase` fields

### Async / Await

- All async methods return `Task` or `Task<T>` — never `async void` (except event handlers)
- Always suffix async methods with `Async`
- Do **not** use `ConfigureAwait(false)` — this is an application, not a library
- `CancellationToken` is threaded through at the infrastructure boundary (`BackgroundService.ExecuteAsync`); inner service methods generally do not require it unless doing I/O loops

### Error Handling

- Wrap startup in `try/catch` with `Log.Fatal` — already done in `Program.cs`, do not remove
- Infrastructure output methods (e.g., `TelegramOutputService`) should catch specific, expected exceptions (like `ApiRequestException`) and log as `LogWarning`, allowing the bot to continue
- Do not swallow unknown exceptions silently — log them at `LogError` or rethrow
- Avoid empty `catch` blocks

### Logging

- Use `ILogger<T>` injected via constructor (Serilog backs it)
- Use structured logging with message templates — **not** string interpolation:
  ```csharp
  // Correct
  _logger.LogInformation("Received command '{Command}' from {UserId}", command, userId);
  // Wrong
  _logger.LogInformation($"Received command '{command}' from {userId}");
  ```
- Log levels: `LogDebug` for trace/diagnostic, `LogInformation` for normal flow, `LogWarning` for recoverable issues, `LogError`/`Log.Fatal` for failures

### Collections & Thread Safety

- `UserSession` mutable collections (`SelectedFiles`, `PendingCommand`) are **not thread-safe** — this is a known issue; do not add new unsynchronized shared state
- For new shared dictionaries, prefer `ConcurrentDictionary<,>` (already used for `PathMap` and sessions)

### SQL / Data Access

- Use `Microsoft.Data.Sqlite` with `await using var conn = new SqliteConnection(...)` for direct queries
- Use `Dapper` for multi-row reads that map to model classes
- SQL statements go in verbatim string literals (`@"..."`)
- Use parameterized queries — never string-concatenate user input into SQL
- Soft-delete only: set `Status = 'Deleted'`, never `DELETE FROM`

### Markdown in Telegram Messages

- Plain messages: `ParseMode.MarkdownV2` — escape all special characters with `EscapeMarkdownV2()`
- Messages with inline keyboards: `ParseMode.Markdown`
- Do not mix the two parse modes

### General

- `partial` classes are used for `CommandAppService` and `FileSystemBrowser` — keep related partials together and clearly named
- XML doc comments (`/// <summary>`) are only on interface methods in `ITelegramOutputService`; add them to new interface methods
- Use `required` keyword on model properties that must always be set: `public required string FullPath { get; set; }`
- Prefer `??` and `?? throw new InvalidOperationException(...)` over unchecked null dereferences
- Regex patterns: use `[GeneratedRegex]` attribute with `partial` method for compiled regexes

---

## Known Issues (Do Not Worsen)

- `UserSession` collections are accessed from concurrent handlers without locks — do not add more non-concurrent collections to `UserSession`
- Bot token should come from configuration, not be hardcoded — always use `IConfiguration`
- `DeleteSessionAsync` uses raw SQL `BEGIN TRANSACTION` string instead of `connection.BeginTransaction()` — use the proper API in new transaction code
