# AGENTS.md

Guidance for agentic coding agents working in this repository.

## Project Overview

Single .NET 8 background service (`TelegramBotServer`) — a Telegram bot using long-polling.
No webhooks, no MVC controllers, no test projects. All services are **Singletons**.

---

## Build & Run Commands

```bash
# Build (use this to verify changes — there are no automated tests)
dotnet build TelegramBotServer/TelegramBotServer.csproj

# Run (requires appsettings.Local.json with bot token, or env var TelegramBot__Token)
dotnet run --project TelegramBotServer/TelegramBotServer.csproj

# Release publish
dotnet publish TelegramBotServer/TelegramBotServer.csproj -c Release

# Format code (no .editorconfig exists — uses SDK defaults)
dotnet format
```

**There are no automated tests.** After making changes, verify correctness by building successfully.

---

## Configuration

- `appsettings.json` — committed, contains Serilog config and `FileSystem` options
- `appsettings.Local.json` — **gitignored**, put secrets here (bot token, local overrides)
- Required config keys:
  - `TelegramBot:Token` — bot token (also settable via env var `TelegramBot__Token`)
  - `FileSystem:RootPath` — filesystem browser root (validated on startup via `FileSystemOptions`)
  - `ConnectionStrings:Sqlite` — SQLite connection string (defaults to `"Data Source=botdata.db"`)

---

## Architecture & Request Flow

```
Telegram API -> TelegramBotHostedService (polling)
             -> TelegramUpdateMapper (Update -> MessageDto | CallbackQueryDto)
             -> Authorization check (AuthService + SessionManager)
             -> CommandAppService.HandleUserCommandAsync (text commands)
             -> CallbackDispatcher.DispatchAsync (inline keyboard callbacks)
```

DI is wired in `DependencyInjectionExtensions.cs`. The filesystem root comes from `FileSystemOptions` (bound to `"FileSystem"` config section).

### Callback Handling — Chain of Responsibility

`CallbackDispatcher` routes callbacks to the first `ICallbackHandler` that `CanHandle()` the prefix (sorted by `Priority`, lower = first). All handlers extend `CallbackHandlerBase`.

Handler hierarchy: `FileNavigationHandler` (10) > `FileSelectionHandler` (20) > `ExportCommandHandler`, `AutomationCommandHandler`, `SessionManagementHandler`, `CommandSelectionHandler` (100).

Callback prefixes are constants in `CallbackPrefixes` (`Models/CallbackPrefixes.cs`). Use `CallbackDataParser.Parse(data)` to get a `ParsedCallback`, then match with `parsed.Is(CallbackPrefixes.OpenFolder)`.

### Database

Tables: `Sessions`, `Commands`, `Whitelist`, `Credentials`.
Soft-delete only — set `Status = 'Deleted'`, never `DELETE FROM`.
DB file: `botdata.db`. Initialized at startup via `host.InitializeDatabaseAsync()`.

---

## Code Style Guidelines

### C# Language Features

- **Target framework**: .NET 8 (`net8.0`)
- **Nullable reference types**: enabled — always annotate nullability (`string?`, `T?`)
- **Implicit usings**: enabled — do not add `using System;` etc. unless needed beyond the implicit set
- **File-scoped namespaces** preferred: `namespace TelegramBotServer.Services;`
  (Some older files use block-scoped — do not perpetuate; use file-scoped for new code)
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

### Imports / Using Directives

- Place `using` directives at the top of the file, before the namespace
- Order: framework namespaces, then third-party (`Dapper`, `Serilog`, `Telegram.Bot`), then project-internal (`TelegramBotServer.*`)
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
  // Correct
  _logger.LogInformation("Received command '{Command}' from {UserId}", command, userId);
  // Wrong
  _logger.LogInformation($"Received command '{command}' from {userId}");
  ```
- Log levels: `LogDebug` for diagnostics, `LogInformation` for normal flow, `LogWarning` for recoverable issues, `LogError` / `Log.Fatal` for failures

### Collections & Thread Safety

- `UserSession` uses fine-grained locks (`_commandLock`, `_selectionLock`, `_navigationLock`, `_messageLock`) — follow this pattern for new mutable state
- `PathMap` uses `ConcurrentDictionary<string, string>`
- For new shared dictionaries, prefer `ConcurrentDictionary<,>`

### SQL / Data Access

- Use `await using var conn = new SqliteConnection(...)` — open a fresh connection per method
- Use `Dapper` for multi-row reads that map to model classes; raw `SqliteCommand` for simple queries
- SQL statements go in verbatim string literals (`@"..."`)
- Use parameterized queries — never string-concatenate user input into SQL
- Soft-delete only: `SET Status = 'Deleted'`, never `DELETE FROM`
- For transactions, use `conn.BeginTransactionAsync()` — not raw SQL `BEGIN TRANSACTION`

### Telegram Messages

- Plain messages: `ParseMode.MarkdownV2` — escape special characters with `EscapeMarkdownV2()`
- Messages with inline keyboards: `ParseMode.Markdown`
- Do not mix the two parse modes
- All Telegram API methods must be current — do not use deprecated approaches

### General

- `partial` classes are used for `CommandAppService` and `FileSystemBrowser`
- XML doc comments (`/// <summary>`) on new interface methods
- Use `required` keyword on model properties that must always be set
- Prefer `??` and `?? throw new InvalidOperationException(...)` over unchecked null dereferences
- Use `[GeneratedRegex]` attribute with `partial` method for compiled regexes
- Maintain good code readability and unify methods for easier editing

---

## Known Issues (Do Not Worsen)

- Bot token is committed in `appsettings.json` — always use `IConfiguration`, never hardcode tokens
- No `.editorconfig` exists despite some tooling expecting it — `dotnet format` uses SDK defaults
- No CI/CD pipeline or automated tests — the only verification is a successful `dotnet build`
