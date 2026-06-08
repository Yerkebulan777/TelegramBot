# AGENTS.md

Guidance for agentic coding agents working in this repository.

## Project Overview

Telegram bot using long-polling, split into **4 projects** (`.slnx`). No webhooks, no MVC controllers. All services are **Singletons**.

```
TelegramBot.Core   ←──  TelegramBot.Data
       ↑                       ↑
       ├──── TelegramBot.Server ──┘
       │
       └──── TelegramBot.Worker
                └── BimLib/ (BIM-интеграция)
```

- **TelegramBot.Core** — Models, DTOs, interfaces, config, constants. Zero Telegram SDK dependency.
- **TelegramBot.Data** — PostgreSQL persistence via Dapper + Npgsql. References Core only. SQL constants in `Sql/` (4 partial files).
- **TelegramBot.Server** — Telegram infrastructure, application services, handlers, hosting, helpers. References Core + Data.
- **TelegramBot.Worker** — Background service for executing Revit/Navisworks/AI tasks. Uses PostgreSQL LISTEN/NOTIFY. References Core + Data. BimLib is embedded inside this project as `Worker/BimLib/` (not a separate project).

> **Note:** BimLib is **not a separate project** — it lives as a directory inside Worker (`TelegramBot.Worker/BimLib/`). Namespaces remain `TelegramBot.BimLib.*`. OpenMcdf dependency is in Worker's `.csproj`.

---

## Build & Run Commands

```bash
# Build all projects (use this to verify changes)
dotnet build TelegramBot.slnx

# Run the server
dotnet run --project TelegramBot.Server/TelegramBot.Server.csproj

# Run the worker (separate terminal)
dotnet run --project TelegramBot.Worker/TelegramBot.Worker.csproj

# Release publish
dotnet publish TelegramBot.Server/TelegramBot.Server.csproj -c Release

# Format code (.editorconfig exists with naming rules, see Known Issues)
dotnet format TelegramBot.slnx
```

**Tests are intentionally disabled for this project.** Do not add test projects, do not add unit/integration tests, and do not run `dotnet test`. After making changes, verify correctness by building successfully with `dotnet build TelegramBot.slnx`.

> **Roadmap:** См. [ROADMAP.md](ROADMAP.md) для полной дорожной карты проекта.

---

## Configuration

- `TelegramBot.Server/appsettings.json` — committed, contains Serilog config, `FileSystem` options, and `ConnectionStrings:Postgres`
- `TelegramBot.Server/appsettings.Local.json` — **gitignored**, put secrets here (bot token, local overrides)
- `TelegramBot.Worker/appsettings.json` — committed, contains `ConnectionStrings:Postgres`
- Required config keys:
  - `TelegramBot:Token` — bot token (also settable via env var `TelegramBot__Token`)
  - `TelegramBot:AdminUserIds` — long[] of admin Telegram IDs (also settable via `TelegramBot__AdminUserIds__0`, `__1`, etc.)
  - `FileSystem:RootPath` — filesystem browser root (validated on startup via `FileSystemOptions`)
  - `ConnectionStrings:Postgres` — PostgreSQL connection string (defaults to `"Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres"`)

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

### BimLib (BIM Integration) — embedded in Worker

BimLib is a **Windows-only** set of modules located inside the Worker project (`TelegramBot.Worker/BimLib/`). It provides BIM-related infrastructure used by `CommandExecutionService`.

**Structure:**

| Folder | Contents |
|--------|----------|
| `Config/` | `BimIntegrationOptions` — min/max supported Revit version, install root path |
| `Interfaces/` | `IRevitVersionDetector`, `INavisworksPathResolver` |
| `Models/` | `RevitDetectedVersion`, `RevitProcessHealth` (status: Healthy/NotResponding/Error) |
| `Monitor/` | `RevitProcessTracker`, `NavisworksProcessTracker`, `ProcessHealthHelper`, `DialogDismisser`, `WindowUtil`, `WindowInfo` |
| `Native/` | P/Invoke WinAPI declarations: `User32`, `Win32Consts` |
| `Services/` | `RevitVersionDetector`, `RevitPathResolver`, `NavisworksPathResolver` |

**Key services:**

| Service | Role |
|---------|------|
| `RevitVersionDetector` | Reads OLE stream `BasicFileInfo` from .rvt/.rfa via OpenMcdf to extract `Format: YYYY` |
| `RevitPathResolver` | Finds `Revit.exe` path via Windows Registry (`HKLM\SOFTWARE\Autodesk\Revit\{version}`) |
| `NavisworksPathResolver` | Finds `Navisworks.exe`/`FileConvert.exe` via Windows Registry |
| `RevitProcessTracker` | Monitors Revit processes: responsiveness, dialog dismissal, PID tracking (uses `ProcessHealthHelper`) |
| `NavisworksProcessTracker` | Monitors Navisworks processes (Roamer, FileConvert) (uses `ProcessHealthHelper`) |
| `ProcessHealthHelper` | Static helper for `CheckHealth()` — shared between both process trackers |
| `DialogDismisser` | Auto-closes modal Revit dialogs (#32770) by finding and clicking known buttons |

**DI registration:** BimLib services are registered directly in `Worker/Program.cs` (no separate `AddBimIntegration()` extension method):
```csharp
services.AddSingleton<IRevitVersionDetector, RevitVersionDetector>();
services.AddSingleton<RevitPathResolver>();
services.AddSingleton<RevitProcessTracker>();
services.AddSingleton<DialogDismisser>();
services.AddSingleton<INavisworksPathResolver, NavisworksPathResolver>();
services.AddSingleton<NavisworksProcessTracker>();
```
Requires `BimIntegrationOptions` config section in Worker's `appsettings.json`.

**Namespaces:**
- `TelegramBot.BimLib.Config`
- `TelegramBot.BimLib.Interfaces`
- `TelegramBot.BimLib.Models`
- `TelegramBot.BimLib.Monitor`
- `TelegramBot.BimLib.Native`
- `TelegramBot.BimLib.Services`

**Important notes for AI agents:**
- BimLib is `[SupportedOSPlatform("windows")]` — never run or test on non-Windows.
- OpenMcdf 3.x is used to parse OLE Structured Storage (.rvt files). API: `RootStorage.OpenRead()` → `root.OpenStream()` → `stream.Read()`.
- Registry access uses `Microsoft.Win32.Registry` — only works on Windows.
- All P/Invoke is in `Native/` (User32 for window operations).
- `RevitProcessStatus` enum has only 3 values: `Healthy`, `NotResponding`, `Error`.
- Removed interfaces (concrete classes only): `IRevitPathResolver`, `IRevitProcessTracker`, `INavisworksProcessTracker` — they had no consumers outside BimLib.
- `ProcessHealthHelper.CheckHealth()` provides shared health-check logic for both `RevitProcessTracker` and `NavisworksProcessTracker`.
- BimLib is **not a separate project** — it lives as a directory inside Worker. No `TelegramBot.BimLib.csproj` exists.

### Shared Static Helpers

| Helper | Location | Purpose |
|--------|----------|---------|
| `HandlerHelpers` | `Server/Services/Application/Handlers/HandlerHelpers.cs` | `SendActionsReplyKeyboardAsync()` — универсальный метод для отправки reply-клавиатуры с трекингом сообщения, заменяет 3 дублированных метода |
| `ProcessHealthHelper` | `BimLib/Monitor/ProcessHealthHelper.cs` | `CheckHealth()` — общая логика проверки здоровья процесса для Revit и Navisworks |
| `NpgsqlHelper` | `TelegramBot.Data/NpgsqlHelper.cs` | `CreateOpenConnectionAsync()` — устраняет дублирование `new NpgsqlConnection + OpenAsync` |

### Task Execution Flow (Server → PostgreSQL → Worker)

Полная спецификация алгоритма: **[Docs/execution-algorithm.md](Docs/execution-algorithm.md)**

```
SlashCommandService.ConfirmFileSelectionAsync()
    │
    ├── dataService.CreateSessionWithCommandsAsync() -- INSERT INTO Commands (ProjectName)
    └── dataService.NotifyNewCommandsAsync() ---------- NOTIFY new_command
                                                              │
                   ┌──────────────────────────────────────────┘
                   ▼
    CommandExecutionService (Worker)
        conn.WaitAsync() -- просыпается мгновенно
        dataService.ClaimPendingCommandsAsync() -- FOR UPDATE SKIP LOCKED
        ExecuteOneAsync(cmd) -- запуск Revit/Navisworks/AI
        dataService.UpdateCommandStatusAsync() -- UPDATE Status='Done'/'Failed'
        TryNotifySessionCompletedAsync() -- только для последней команды сессии
            └── dataService.NotifyCommandCompletedAsync() -- NOTIFY command_completed
                                                              │
                   ┌──────────────────────────────────────────┘
                   ▼
    CommandNotificationService (Server)
        Получает NOTIFY → парсит payload (UserId|SessionId|Done|Total|ProjectName)
        При наличии ошибок → запрашивает список Failed-файлов из БД
        → telegramOutput.SendMessageAsync() со сводкой по сессии
```

**In-memory счётчик сессий:** вместо per-command SQL запроса `GetSessionProgressAsync`
Worker использует `ConcurrentDictionary<int, int> _sessionRemaining`.
При `ClaimPendingCommandsAsync` счётчик заполняется по `GroupBy(SessionId)`,
при завершении каждой команды атомарно декрементится через `AddOrUpdate`.
Уведомление отправляется только когда `remaining == 0`.

DI is wired in `TelegramBot.Server/Extensions/DependencyInjectionExtensions.cs`. The filesystem root comes from `FileSystemOptions` (bound to `"FileSystem"` config section). The Worker uses `PostgresDataService` registered directly in `Program.cs`.

**Key DI simplification:** `IFileSystemBrowser` and `ITelegramUpdateMapper` interfaces were removed — their consumers now depend on concrete types `FileSystemBrowser` and `TelegramUpdateMapper` directly (no testability requirement for these internal services).

### Callback Handling — Chain of Responsibility

`CallbackDispatcher` (implements `ICallbackDispatcher`) routes callbacks to the first `ICallbackHandler` that `CanHandle()` the prefix (sorted by `Priority`, lower = first). All handlers extend `CallbackHandlerBase`.

Handler hierarchy: `AccessRequestHandler` (Priority 0) > `FileNavigationHandler` (10) > `FileSelectionHandler` (20) > `CommandToggleHandler`, `SessionManagementHandler`, `CommandSelectionHandler` (100).

**Error handling:** `CallbackHandlerBase.HandleAsync()` does NOT catch exceptions — they propagate to `CallbackDispatcher.DispatchAsync()`, which catches `Exception`, logs it, and continues to the next handler. This eliminates double logging.

Callback prefixes are constants in `CallbackPrefixes` (`TelegramBot.Core/Models/CallbackPrefixes.cs`). Command codes in `TelegramBot.Core/Constants/CommandCodes.cs`. Use `CallbackDataParser.Parse(data)` (from `ParsedCallback.cs`) to get a `ParsedCallback`, then match with `parsed.Is(CallbackPrefixes.GoToParent)`. 

> **SessionManagementHandler** manages `/status` actions via `SESSIONDETAILS:`, `DELETESESSION:`, and `DELETECOMMAND:`. The «⛔ Отменить» button for a running command uses the same soft-delete path as command deletion: `Status = 'Deleted'`.

For Markdown escaping, use `MarkdownHelper` from `TelegramBot.Server/Helpers/`.

### Database

Tables: `BotUsers`, `Sessions`, `Commands`, `TrackedMessages`. Message tracking is fully DB-backed — no in-memory state. Soft-delete only — set `Status = 'Deleted'`, never `DELETE FROM`.

**`Sessions` table now includes `ProjectName TEXT`** — имя проекта записывается при создании сессии,
отображается в `/status` и в уведомлениях о завершении.

**`GetCommandStatusAsync` removed** — was dead code. Deleted commands never appear as `'pending'`
in `ClaimPendingCommandsAsync`, so the separate cancellation check was redundant.

**`CountPendingProcessingBySessionAsync` added** — используется в `TryNotifySessionCompletedAsync`
для проверки, не осталось ли ещё pending/processing команд в БД (корректно обрабатывает случай,
когда команд в сессии > DefaultBatchSize).

Database: **PostgreSQL** via Npgsql. Initialized at startup via `host.InitializeDatabaseAsync()` + `host.SeedAdminUsersAsync()`.
All data access uses **Dapper** (`TelegramBot.Data/PostgresDataService.cs`). Connection creation is unified via `CreateConnectionAsync()` helper (replaces ~15 manual `new NpgsqlConnection + OpenAsync` patterns). SQL constants in `TelegramBot.Data/Sql/` (4 partial files total).

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
- `TelegramBot.Worker.Services`

### Imports / Using Directives

- Place `using` directives at the top of the file, before the namespace
- Order: framework namespaces, then third-party (`Dapper`, `Npgsql`, `Serilog`, `Telegram.Bot`), then project-internal (`TelegramBot.*`)
- Do not add unnecessary usings

### Dependency Injection

- Register all new services as **Singletons** in `DependencyInjectionExtensions.cs`
- Use `_ = services.AddSingleton<IFoo, Foo>()` (discard the fluent return value)
- Inject dependencies via **primary constructors** (C# 12). Parameters are captured automatically — do NOT add redundant `private readonly` fields for direct copies:
  ```csharp
  // ✅ CORRECT — no redundant fields
  public sealed class Foo(IBar bar, ILogger<Foo> logger) : IFoo
  {
      public void DoWork() => bar.DoSomething();
  }
  
  // ❌ WRONG — fields are redundant
  public sealed class Foo(IBar bar, ILogger<Foo> logger) : IFoo
  {
      private readonly IBar _bar = bar;  // DELETE this
  }
  ```
- Fields that **transform** parameters are fine: `private readonly FileSystemOptions _options = options.Value;`
- Fields that create **new instances** are fine: `private readonly ConcurrentDictionary<int, Process> _activeProcesses = new();`
- Protected fields exposed to subclasses are fine: `protected readonly ILogger Logger = logger;`
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
- Callback exceptions: `CallbackDispatcher` catches + logs; **`CallbackHandlerBase` does not** (no double logging)
- Worker: outer retry loop reconnects on PostgreSQL connection loss (5 sec delay)

### Logging

- Use `ILogger<T>` injected via constructor (Serilog backs it)
- Use structured logging with message templates — **not** string interpolation:
  ```csharp
  _logger.LogInformation("Received command '{Command}' from {UserId}", command, userId);
  ```
- Log levels: `LogDebug` for diagnostics, `LogInformation` for normal flow, `LogWarning` for recoverable issues, `LogError` / `Log.Fatal` for failures

### Collections & Thread Safety

- `UserSession` uses fine-grained locks (`_commandLock`, `_selectionLock`) — follow this pattern for new mutable state
- Paths in callback data are passed directly (no `PathMap`/tokens) since v1.1 refactoring
- For new shared dictionaries, prefer `ConcurrentDictionary<,>`

### SQL / Data Access (TelegramBot.Data)

- Use `await using var conn = await CreateConnectionAsync()` — connection creation is unified via a private helper in `PostgresDataService`
- Use **Dapper** for all queries (no raw `NpgsqlCommand`/`NpgsqlDataReader`)
- SQL statements go in verbatim string literals (`@"..."`)
- Use parameterized queries — never string-concatenate user input into SQL
- Soft-delete only: `SET Status = 'Deleted'`, never `DELETE FROM`
- For transactions, use `conn.BeginTransactionAsync()`
- Use `RETURNING` clause for INSERT to get generated IDs (not `last_insert_rowid()`)
- Use `ON CONFLICT DO NOTHING / DO UPDATE` for upserts (not `INSERT OR IGNORE/REPLACE`)
- PostgreSQL data types: `TIMESTAMPTZ` for dates, `SERIAL` for auto-increment, `BIGINT` for user IDs

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
- Extract shared static helpers (`HandlerHelpers`, `NpgsqlHelper`) when the same 5+ line pattern appears in multiple files
- Use `dotnet format --diagnostics IDE0005` to remove unused `using` directives

---

## Known Issues (Do Not Worsen)

- `.editorconfig` exists with naming rules, formatting preferences, and `generated_code = true` markers for data service and handlers — `dotnet format` respects these
- No CI/CD pipeline or automated tests — the only verification is a successful `dotnet build`
- Keep secrets out of committed config files — use `TelegramBot.Server/appsettings.Local.json` (gitignored) or env var `TelegramBot__Token`; never hardcode tokens
- PostgreSQL connection string in committed `appsettings.json` uses default `postgres/postgres` credentials — override via `appsettings.Local.json` or env var `ConnectionStrings__Postgres`
- `/// <inheritdoc/>` comments on methods that no longer implement interfaces (e.g., `RevitPathResolver`, `RevitProcessTracker`) are stale but harmless — replace with proper `<summary>` when editing nearby

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **TelegramBot** (1383 symbols, 3461 relationships, 115 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

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
