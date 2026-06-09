# Документные утверждения (Doc Claims)

> Сгенерировано: 2026-06-09. Источники: `AGENTS.md`, `README.md`, `ROADMAP.md`, `Docs/execution-algorithm.md`, `Docs/CommandExecutionAlgorithm.md`, `Docs/qodana-setup.md`, `.github/copilot-instructions.md`.
>
> Каждое утверждение — точная цитата (verbatim) с указанием файла и строки/диапазона. Категории: `[architecture | class-name | namespace | config-key | sql-table | sql-field | callback-prefix | command-code | dependency | build-cmd | run-cmd | other]`.
>
> Сверочный агент должен проверять каждое утверждение по фактическому коду/файлам.

---

## AGENTS.md (AGENTS-###)

### AGENTS-001 — 4 проекта, .slnx, no webhooks, no MVC, Singletons
- **Файл:** `AGENTS.md:7`
- **Категория:** architecture
- **Цитата:** "Telegram bot using long-polling, split into **4 projects** (`.slnx`). No webhooks, no MVC controllers. All services are **Singletons**."

### AGENTS-002 — Диаграмма зависимостей проектов (Core, Data, Server, Worker, BimLib)
- **Файл:** `AGENTS.md:9-16`
- **Категория:** architecture
- **Цитата:**
  ```
  TelegramBot.Core   ←──  TelegramBot.Data
         ↑                       ↑
         ├──── TelegramBot.Server ──┘
         │
         └──── TelegramBot.Worker
                  └── BimLib/ (BIM-интеграция)
  ```

### AGENTS-003 — TelegramBot.Core — назначение
- **Файл:** `AGENTS.md:18`
- **Категория:** architecture
- **Цитата:** "**TelegramBot.Core** — Models, DTOs, interfaces, config, constants. Zero Telegram SDK dependency."

### AGENTS-004 — TelegramBot.Data — назначение и стек
- **Файл:** `AGENTS.md:19`
- **Категория:** architecture
- **Цитата:** "**TelegramBot.Data** — PostgreSQL persistence via Dapper + Npgsql. References Core only. SQL constants in `Sql/` (4 partial files)."

### AGENTS-005 — TelegramBot.Server — назначение
- **Файл:** `AGENTS.md:20`
- **Категория:** architecture
- **Цитата:** "**TelegramBot.Server** — Telegram infrastructure, application services, handlers, hosting, helpers. References Core + Data."

### AGENTS-006 — TelegramBot.Worker — назначение
- **Файл:** `AGENTS.md:21`
- **Категория:** architecture
- **Цитата:** "**TelegramBot.Worker** — Background service for executing Revit/Navisworks/AI tasks. Polls PostgreSQL for pending commands. References Core + Data. BimLib is embedded inside this project as `Worker/BimLib/` (not a separate project)."

### AGENTS-007 — BimLib не отдельный проект, namespace prefix
- **Файл:** `AGENTS.md:23`
- **Категория:** architecture
- **Цитата:** "BimLib is **not a separate project** — it lives as a directory inside Worker (`TelegramBot.Worker/BimLib/`). Namespaces remain `TelegramBot.BimLib.*`. OpenMcdf dependency is in Worker's `.csproj`."

### AGENTS-008 — Команда сборки
- **Файл:** `AGENTS.md:31`
- **Категория:** build-cmd
- **Цитата:** "dotnet build TelegramBot.slnx"

### AGENTS-009 — Команда запуска Server
- **Файл:** `AGENTS.md:34`
- **Категория:** run-cmd
- **Цитата:** "dotnet run --project TelegramBot.Server/TelegramBot.Server.csproj"

### AGENTS-010 — Команда запуска Worker
- **Файл:** `AGENTS.md:37`
- **Категория:** run-cmd
- **Цитата:** "dotnet run --project TelegramBot.Worker/TelegramBot.Worker.csproj"

### AGENTS-011 — Команда publish
- **Файл:** `AGENTS.md:40`
- **Категория:** build-cmd
- **Цитата:** "dotnet publish TelegramBot.Server/TelegramBot.Server.csproj -c Release"

### AGENTS-012 — Команда форматирования
- **Файл:** `AGENTS.md:43`
- **Категория:** build-cmd
- **Цитата:** "dotnet format TelegramBot.slnx"

### AGENTS-013 — Тесты отключены, do not run dotnet test
- **Файл:** `AGENTS.md:46`
- **Категория:** other
- **Цитата:** "**Tests are intentionally disabled for this project.** Do not add test projects, do not add unit/integration tests, and do not run `dotnet test`. After making changes, verify correctness by building successfully with `dotnet build TelegramBot.slnx`."

### AGENTS-014 — Server appsettings.json коммитится, содержит Serilog/FileSystem/Postgres
- **Файл:** `AGENTS.md:54`
- **Категория:** config-key
- **Цитата:** "`TelegramBot.Server/appsettings.json` — committed, contains Serilog config, `FileSystem` options, and `ConnectionStrings:Postgres`"

### AGENTS-015 — appsettings.Local.json gitignored
- **Файл:** `AGENTS.md:55`
- **Категория:** other
- **Цитата:** "`TelegramBot.Server/appsettings.Local.json` — **gitignored**, put secrets here (bot token, local overrides)"

### AGENTS-016 — Worker appsettings.json коммитится с ConnectionStrings:Postgres
- **Файл:** `AGENTS.md:56`
- **Категория:** config-key
- **Цитата:** "`TelegramBot.Worker/appsettings.json` — committed, contains `ConnectionStrings:Postgres`"

### AGENTS-017 — TelegramBot:Token (config key, env var)
- **Файл:** `AGENTS.md:58`
- **Категория:** config-key
- **Цитата:** "`TelegramBot:Token` — bot token (also settable via env var `TelegramBot__Token`)"

### AGENTS-018 — TelegramBot:AdminUserIds (long[])
- **Файл:** `AGENTS.md:59`
- **Категория:** config-key
- **Цитата:** "`TelegramBot:AdminUserIds` — long[] of admin Telegram IDs (also settable via `TelegramBot__AdminUserIds__0`, `__1`, etc.)"

### AGENTS-019 — FileSystem:RootPath валидируется через FileSystemOptions
- **Файл:** `AGENTS.md:60`
- **Категория:** config-key
- **Цитата:** "`FileSystem:RootPath` — filesystem browser root (validated on startup via `FileSystemOptions`)"

### AGENTS-020 — ConnectionStrings:Postgres default
- **Файл:** `AGENTS.md:61`
- **Категория:** config-key
- **Цитата:** "`ConnectionStrings:Postgres` — PostgreSQL connection string (defaults to `\"Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres\"`)"

### AGENTS-021 — RateLimit:MaxFilesPerUserPerDay
- **Файл:** `AGENTS.md:62`
- **Категория:** config-key
- **Цитата:** "`RateLimit:MaxFilesPerUserPerDay` — daily per-user file quota; `0` disables it"

### AGENTS-022 — Worker:CompletedSessionRetentionDays
- **Файл:** `AGENTS.md:63`
- **Категория:** config-key
- **Цитата:** "`Worker:CompletedSessionRetentionDays` — auto-cleanup retention for inactive sessions; `0` disables it"

### AGENTS-023 — Request flow (TelegramUpdateMapper, CommandAppService, CallbackDispatcher)
- **Файл:** `AGENTS.md:70-78`
- **Категория:** architecture
- **Цитата:**
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

### AGENTS-024 — REQACCESS/APPROVEUSER/REJECTUSER префиксы bypass access check
- **Файл:** `AGENTS.md:76-77`
- **Категория:** callback-prefix
- **Цитата:** "REQACCESS/APPROVEUSER/REJECTUSER bypass access check" / "all other callbacks → user must be Approved"

### AGENTS-025 — BimLib Windows-only, в Worker
- **Файл:** `AGENTS.md:82`
- **Категория:** architecture
- **Цитата:** "BimLib is a **Windows-only** set of modules located inside the Worker project (`TelegramBot.Worker/BimLib/`). It provides BIM-related infrastructure used by `CommandExecutionService`."

### AGENTS-026 — BimLib Config: BimIntegrationOptions
- **Файл:** `AGENTS.md:88`
- **Категория:** class-name
- **Цитата:** "`Config/` | `BimIntegrationOptions` — min/max supported Revit version, install root path"

### AGENTS-027 — BimLib Interfaces
- **Файл:** `AGENTS.md:89`
- **Категория:** class-name
- **Цитата:** "`Interfaces/` | `IRevitVersionDetector`, `INavisworksPathResolver`"

### AGENTS-028 — BimLib Models
- **Файл:** `AGENTS.md:90`
- **Категория:** class-name
- **Цитата:** "`Models/` | `RevitDetectedVersion`, `RevitProcessHealth` (status: Healthy/NotResponding/Error)"

### AGENTS-029 — BimLib Monitor
- **Файл:** `AGENTS.md:91`
- **Категория:** class-name
- **Цитата:** "`Monitor/` | `RevitProcessTracker`, `NavisworksProcessTracker`, `ProcessHealthHelper`, `DialogDismisser`, `WindowUtil`, `WindowInfo`"

### AGENTS-030 — BimLib Native
- **Файл:** `AGENTS.md:92`
- **Категория:** class-name
- **Цитата:** "`Native/` | P/Invoke WinAPI declarations: `User32`, `Win32Consts`"

### AGENTS-031 — BimLib Services
- **Файл:** `AGENTS.md:93`
- **Категория:** class-name
- **Цитата:** "`Services/` | `RevitVersionDetector`, `RevitPathResolver`, `NavisworksPathResolver`"

### AGENTS-032 — RevitVersionDetector: OpenMcdf → Format: YYYY
- **Файл:** `AGENTS.md:99`
- **Категория:** class-name
- **Цитата:** "`RevitVersionDetector` | Reads OLE stream `BasicFileInfo` from .rvt/.rfa via OpenMcdf to extract `Format: YYYY`"

### AGENTS-033 — RevitPathResolver registry path
- **Файл:** `AGENTS.md:100`
- **Категория:** other
- **Цитата:** "`RevitPathResolver` | Finds `Revit.exe` path via Windows Registry (`HKLM\\SOFTWARE\\Autodesk\\Revit\\{version}`)"

### AGENTS-034 — DialogDismisser — авто-закрытие #32770
- **Файл:** `AGENTS.md:105`
- **Категория:** class-name
- **Цитата:** "`DialogDismisser` | Auto-closes modal Revit dialogs (#32770) by finding and clicking known buttons"

### AGENTS-035 — DI-регистрация BimLib в Worker/Program.cs (6 строк)
- **Файл:** `AGENTS.md:108-115`
- **Категория:** architecture
- **Цитата:**
  ```csharp
  services.AddSingleton<IRevitVersionDetector, RevitVersionDetector>();
  services.AddSingleton<RevitPathResolver>();
  services.AddSingleton<RevitProcessTracker>();
  services.AddSingleton<DialogDismisser>();
  services.AddSingleton<INavisworksPathResolver, NavisworksPathResolver>();
  services.AddSingleton<NavisworksProcessTracker>();
  ```

### AGENTS-036 — Требуется BimIntegrationOptions config section
- **Файл:** `AGENTS.md:116`
- **Категория:** config-key
- **Цитата:** "Requires `BimIntegrationOptions` config section in Worker's `appsettings.json`."

### AGENTS-037 — Namespaces BimLib
- **Файл:** `AGENTS.md:119-124`
- **Категория:** namespace
- **Цитата:** "TelegramBot.BimLib.Config / Interfaces / Models / Monitor / Native / Services"

### AGENTS-038 — BimLib SupportedOSPlatform("windows")
- **Файл:** `AGENTS.md:127`
- **Категория:** other
- **Цитата:** "BimLib is `[SupportedOSPlatform(\"windows\")]` — never run or test on non-Windows."

### AGENTS-039 — OpenMcdf 3.x API
- **Файл:** `AGENTS.md:128`
- **Категория:** dependency
- **Цитата:** "OpenMcdf 3.x is used to parse OLE Structured Storage (.rvt files). API: `RootStorage.OpenRead()` → `root.OpenStream()` → `stream.Read()`."

### AGENTS-040 — RevitProcessStatus 3 значения
- **Файл:** `AGENTS.md:131`
- **Категория:** other
- **Цитата:** "`RevitProcessStatus` enum has only 3 values: `Healthy`, `NotResponding`, `Error`."

### AGENTS-041 — Удалены интерфейсы без потребителей
- **Файл:** `AGENTS.md:132`
- **Категория:** class-name
- **Цитата:** "Removed interfaces (concrete classes only): `IRevitPathResolver`, `IRevitProcessTracker`, `INavisworksProcessTracker` — they had no consumers outside BimLib."

### AGENTS-042 — ProcessHealthHelper.CheckHealth shared
- **Файл:** `AGENTS.md:133`
- **Категория:** class-name
- **Цитата:** "`ProcessHealthHelper.CheckHealth()` provides shared health-check logic for both `RevitProcessTracker` and `NavisworksProcessTracker`."

### AGENTS-043 — Shared static helpers: HandlerHelpers, ProcessHealthHelper, NpgsqlHelper
- **Файл:** `AGENTS.md:138-142`
- **Категория:** class-name
- **Цитата:**
  | Helper | Location | Purpose |
  |--------|----------|---------|
  | `HandlerHelpers` | `Server/Services/Application/Handlers/HandlerHelpers.cs` | `SendActionsReplyKeyboardAsync()` — универсальный метод для отправки reply-клавиатуры с трекингом сообщения, заменяет 3 дублированных метода |
  | `ProcessHealthHelper` | `BimLib/Monitor/ProcessHealthHelper.cs` | `CheckHealth()` — общая логика проверки здоровья процесса для Revit и Navisworks |
  | `NpgsqlHelper` | `TelegramBot.Data/NpgsqlHelper.cs` | `CreateOpenConnectionAsync()` — устраняет дублирование `new NpgsqlConnection + OpenAsync` |

### AGENTS-044 — Task Execution Flow (Server → PostgreSQL → Worker)
- **Файл:** `AGENTS.md:148-168`
- **Категория:** architecture
- **Цитата:** Подробный flow от `SlashCommandService.ConfirmFileSelectionAsync()` через `dataService.CreateSessionWithCommandsAsync()` (INSERT INTO Commands) → `CommandExecutionService` (poll 1 мин) → `ClaimPendingCommandsAsync` (FOR UPDATE SKIP LOCKED) → `ExecuteOneAsync` → `UpdateCommandStatusAsync` → `CompleteClaimedCommandAsync` → `NotifyCommandCompletedAsync` (NOTIFY command_completed) → `CommandNotificationService` (Server).

### AGENTS-045 — In-memory счётчик _sessionRemaining (ConcurrentDictionary<int,int>)
- **Файл:** `AGENTS.md:170-175`
- **Категория:** architecture
- **Цитата:** "Worker использует `ConcurrentDictionary<int, int> _sessionRemaining` как batch-local оптимизацию. При `ClaimPendingCommandsAsync` счётчик заполняется по `GroupBy(SessionId)`, при каждом выходе захваченной команды из `processing` (Done/Failed/retry) атомарно декрементится через `AddOrUpdate`."

### AGENTS-046 — DI extensions, FileSystemOptions
- **Файл:** `AGENTS.md:177`
- **Категория:** other
- **Цитата:** "DI is wired in `TelegramBot.Server/Extensions/DependencyInjectionExtensions.cs`. The filesystem root comes from `FileSystemOptions` (bound to `\"FileSystem\"` config section). The Worker uses `PostgresDataService` registered directly in `Program.cs`."

### AGENTS-047 — Удалены интерфейсы IFileSystemBrowser, ITelegramUpdateMapper
- **Файл:** `AGENTS.md:179`
- **Категория:** class-name
- **Цитата:** "**Key DI simplification:** `IFileSystemBrowser` and `ITelegramUpdateMapper` interfaces were removed — their consumers now depend on concrete types `FileSystemBrowser` and `TelegramUpdateMapper` directly (no testability requirement for these internal services)."

### AGENTS-048 — Callback Handling — Chain of Responsibility
- **Файл:** `AGENTS.md:183-187`
- **Категория:** architecture
- **Цитата:** "`CallbackDispatcher` (implements `ICallbackDispatcher`) routes callbacks to the first `ICallbackHandler` that `CanHandle()` the prefix (sorted by `Priority`, lower = first). All handlers extend `CallbackHandlerBase`."
  "Handler hierarchy: `AccessRequestHandler` (Priority 0) > `FileNavigationHandler` (10) > `FileSelectionHandler` (20) > `CommandToggleHandler`, `SessionManagementHandler`, `CommandSelectionHandler` (100)."

### AGENTS-049 — Error handling: CallbackHandlerBase не ловит исключения
- **Файл:** `AGENTS.md:187`
- **Категория:** architecture
- **Цитата:** "**Error handling:** `CallbackHandlerBase.HandleAsync()` does NOT catch exceptions — they propagate to `CallbackDispatcher.DispatchAsync()`, which catches `Exception`, logs it, and continues to the next handler. This eliminates double logging."

### AGENTS-050 — CallbackPrefixes и CommandCodes locations
- **Файл:** `AGENTS.md:189`
- **Категория:** class-name
- **Цитата:** "Callback prefixes are constants in `CallbackPrefixes` (`TelegramBot.Core/Models/CallbackPrefixes.cs`). Command codes in `TelegramBot.Core/Constants/CommandCodes.cs`. Use `CallbackDataParser.Parse(data)` (from `ParsedCallback.cs`) to get a `ParsedCallback`, then match with `parsed.Is(CallbackPrefixes.GoToParent)`."

### AGENTS-051 — SessionManagementHandler callback prefixes
- **Файл:** `AGENTS.md:191`
- **Категория:** callback-prefix
- **Цитата:** "**SessionManagementHandler** manages `/status` actions via `SESSIONDETAILS:`, `DELETESESSION:`, `DELETECOMMAND:`, `CONFIRMDELETESESSION:`, and `CONFIRMDELETECOMMAND:`. Delete buttons first show a confirmation dialog; the «⛔ Отменить» button for a running command uses the same soft-delete path as command deletion: `Status = 'Deleted'`."

### AGENTS-052 — MarkdownHelper location
- **Файл:** `AGENTS.md:193`
- **Категория:** class-name
- **Цитата:** "For Markdown escaping, use `MarkdownHelper` from `TelegramBot.Server/Helpers/`."

### AGENTS-053 — 4 таблицы БД, soft-delete only
- **Файл:** `AGENTS.md:197`
- **Категория:** sql-table
- **Цитата:** "Tables: `BotUsers`, `Sessions`, `Commands`, `TrackedMessages`. Message tracking is fully DB-backed — no in-memory state. Soft-delete only — set `Status = 'Deleted'`, never `DELETE FROM`. Worker auto-cleanup also uses soft-delete for inactive sessions older than `Worker:CompletedSessionRetentionDays`."

### AGENTS-054 — Sessions.ProjectName TEXT
- **Файл:** `AGENTS.md:199-200`
- **Категория:** sql-field
- **Цитата:** "**`Sessions` table now includes `ProjectName TEXT`** — имя проекта записывается при создании сессии, отображается в `/status` и в уведомлениях о завершении."

### AGENTS-055 — GetCommandStatusAsync removed
- **Файл:** `AGENTS.md:202-203`
- **Категория:** class-name
- **Цитата:** "**`GetCommandStatusAsync` removed** — was dead code. Deleted commands never appear as `'pending'` in `ClaimPendingCommandsAsync`, so the separate cancellation check was redundant."

### AGENTS-056 — CountPendingProcessingBySessionAsync added
- **Файл:** `AGENTS.md:205-207`
- **Категория:** class-name
- **Цитата:** "**`CountPendingProcessingBySessionAsync` added** — используется в `CompleteClaimedCommandAsync` для проверки, не осталось ли ещё pending/processing команд в БД (корректно обрабатывает случай, когда команд в сессии > DefaultBatchSize)."

### AGENTS-057 — DB init: InitializeDatabaseAsync, SeedAdminUsersAsync
- **Файл:** `AGENTS.md:209`
- **Категория:** architecture
- **Цитата:** "Initialized at startup via `host.InitializeDatabaseAsync()` + `host.SeedAdminUsersAsync()`. All data access uses **Dapper** (`TelegramBot.Data/PostgresDataService.cs`). Connection creation is unified via `CreateConnectionAsync()` helper (replaces ~15 manual `new NpgsqlConnection + OpenAsync` patterns). SQL constants in `TelegramBot.Data/Sql/` (4 partial files total)."

### AGENTS-058 — Target framework .NET 10
- **Файл:** `AGENTS.md:218`
- **Категория:** other
- **Цитата:** "**Target framework**: .NET 10 (`net10.0`)"

### AGENTS-059 — Nullable reference types
- **Файл:** `AGENTS.md:219`
- **Категория:** other
- **Цитата:** "**Nullable reference types**: enabled — always annotate nullability (`string?`, `T?`)"

### AGENTS-060 — Implicit usings
- **Файл:** `AGENTS.md:220`
- **Категория:** other
- **Цитата:** "**Implicit usings**: enabled — do not add `using System;` etc. unless needed beyond the implicit set"

### AGENTS-061 — File-scoped namespaces
- **Файл:** `AGENTS.md:221`
- **Категория:** other
- **Цитата:** "**File-scoped namespaces** required: `namespace TelegramBot.Core.Models;`"

### AGENTS-062 — Primary constructors (C# 12)
- **Файл:** `AGENTS.md:222`
- **Категория:** other
- **Цитата:** "**Primary constructors** (C# 12) are used in newer services; either style is acceptable but be consistent within a file"

### AGENTS-063 — Naming conventions
- **Файл:** `AGENTS.md:226-235`
- **Категория:** other
- **Цитата:** Classes `PascalCase`, Interfaces `I` + `PascalCase`, Methods `PascalCase`, Async suffix `Async`, Private fields `_camelCase`, Properties `PascalCase`, Locals `camelCase`, Parameters `camelCase`.

### AGENTS-064 — Namespace conventions
- **Файл:** `AGENTS.md:239-243`
- **Категория:** namespace
- **Цитата:**
  - `TelegramBot.Core.Models`, `TelegramBot.Core.DTOs`, `TelegramBot.Core.Interfaces`, `TelegramBot.Core.Config`, `TelegramBot.Core.Constants`
  - `TelegramBot.Data`
  - `TelegramBot.Server.Services.Application`, `TelegramBot.Server.Services.Infrastructure.Telegram`, `TelegramBot.Server.Helpers`
  - `TelegramBot.Worker.Services`

### AGENTS-065 — Imports order
- **Файл:** `AGENTS.md:247-248`
- **Категория:** other
- **Цитата:** "Order: framework namespaces, then third-party (`Dapper`, `Npgsql`, `Serilog`, `Telegram.Bot`), then project-internal (`TelegramBot.*`)"

### AGENTS-066 — DI register as Singletons
- **Файл:** `AGENTS.md:253`
- **Категория:** other
- **Цитата:** "Register all new services as **Singletons** in `DependencyInjectionExtensions.cs`"

### AGENTS-067 — DI fluent return discarded
- **Файл:** `AGENTS.md:254`
- **Категория:** other
- **Цитата:** "Use `_ = services.AddSingleton<IFoo, Foo>()` (discard the fluent return value)"

### AGENTS-068 — Primary constructors: не дублировать поля
- **Файл:** `AGENTS.md:255-271`
- **Категория:** other
- **Цитата:** "Inject dependencies via **primary constructors** (C# 12). Parameters are captured automatically — do NOT add redundant `private readonly` fields for direct copies." + примеры ✅/❌.

### AGENTS-069 — Async conventions
- **Файл:** `AGENTS.md:276-279`
- **Категория:** other
- **Цитата:** "All async methods return `Task` or `Task<T>` — never `async void`"; "Always suffix async methods with `Async`"; "Do **not** use `ConfigureAwait(false)` — this is an application, not a library"; "CancellationToken is threaded from `BackgroundService.ExecuteAsync`; inner methods generally do not require it unless doing I/O loops"

### AGENTS-070 — Error handling rules
- **Файл:** `AGENTS.md:282-289`
- **Категория:** other
- **Цитата:** "Startup: wrapped in `try/catch` with `Log.Fatal` in `Program.cs` — do not remove"; "Telegram API calls: catch `ApiRequestException` specifically, log as `LogWarning`, let the bot continue"; "`TelegramOutputService` has retry logic for HTTP 429 (rate limiting) via `ExecuteWithRetryAsync`"; "Do not swallow unknown exceptions — log at `LogError` or rethrow"; "Callback exceptions: `CallbackDispatcher` catches + logs; **`CallbackHandlerBase` does not** (no double logging)"; "Worker: outer retry loop reconnects on PostgreSQL connection loss (5 sec delay)"

### AGENTS-071 — Logging: ILogger<T>, structured logging
- **Файл:** `AGENTS.md:293-298`
- **Категория:** other
- **Цитата:** "Use `ILogger<T>` injected via constructor (Serilog backs it)"; "Use structured logging with message templates — **not** string interpolation"

### AGENTS-072 — UserSession locks pattern
- **Файл:** `AGENTS.md:302`
- **Категория:** other
- **Цитата:** "`UserSession` uses fine-grained locks (`_commandLock`, `_selectionLock`) — follow this pattern for new mutable state"

### AGENTS-073 — Paths in callback data directly (no PathMap)
- **Файл:** `AGENTS.md:303`
- **Категория:** other
- **Цитата:** "Paths in callback data are passed directly (no `PathMap`/tokens) since v1.1 refactoring"

### AGENTS-074 — Dapper only, no raw NpgsqlCommand
- **Файл:** `AGENTS.md:309`
- **Категория:** other
- **Цитата:** "Use **Dapper** for all queries (no raw `NpgsqlCommand`/`NpgsqlDataReader`)"

### AGENTS-075 — SQL conventions
- **Файл:** `AGENTS.md:310-316`
- **Категория:** other
- **Цитата:** "SQL statements go in verbatim string literals (`@\"...\"`)"; "Use parameterized queries — never string-concatenate user input into SQL"; "Soft-delete only: `SET Status = 'Deleted'`, never `DELETE FROM`"; "For transactions, use `conn.BeginTransactionAsync()`"; "Use `RETURNING` clause for INSERT to get generated IDs (not `last_insert_rowid()`)"; "Use `ON CONFLICT DO NOTHING / DO UPDATE` for upserts (not `INSERT OR IGNORE/REPLACE`)"; "PostgreSQL data types: `TIMESTAMPTZ` for dates, `SERIAL` for auto-increment, `BIGINT` for user IDs"

### AGENTS-076 — Telegram messages parse modes
- **Файл:** `AGENTS.md:320-322`
- **Категория:** other
- **Цитата:** "Plain messages: `ParseMode.MarkdownV2` — escape special characters with `MarkdownHelper.EscapeMarkdownV2()`"; "Messages with inline keyboards: `ParseMode.Markdown` — escape with `MarkdownHelper.EscapeMarkdown()`"; "Do not mix the two parse modes"

### AGENTS-077 — Required keyword на моделях
- **Файл:** `AGENTS.md:328`
- **Категория:** other
- **Цитата:** "Use `required` keyword on model properties that must always be set"

### AGENTS-078 — dotnet format IDE0005 для unused usings
- **Файл:** `AGENTS.md:332`
- **Категория:** build-cmd
- **Цитата:** "Use `dotnet format --diagnostics IDE0005` to remove unused `using` directives"

### AGENTS-079 — .editorconfig с generated_code markers
- **Файл:** `AGENTS.md:338`
- **Категория:** other
- **Цитата:** "`.editorconfig` exists with naming rules, formatting preferences, and `generated_code = true` markers for data service and handlers — `dotnet format` respects these"

### AGENTS-080 — Нет CI/CD, только dotnet build
- **Файл:** `AGENTS.md:339`
- **Категория:** other
- **Цитата:** "No CI/CD pipeline or automated tests — the only verification is a successful `dotnet build`"

### AGENTS-081 — Секреты в appsettings.Local.json или env
- **Файл:** `AGENTS.md:340`
- **Категория:** other
- **Цитата:** "Keep secrets out of committed config files — use `TelegramBot.Server/appsettings.Local.json` (gitignored) or env var `TelegramBot__Token`; never hardcode tokens"

### AGENTS-082 — Дефолтные postgres/postgres credentials
- **Файл:** `AGENTS.md:341`
- **Категория:** config-key
- **Цитата:** "PostgreSQL connection string in committed `appsettings.json` uses default `postgres/postgres` credentials — override via `appsettings.Local.json` or env var `ConnectionStrings__Postgres`"

### AGENTS-083 — Stale /// <inheritdoc/> на конкретных классах
- **Файл:** `AGENTS.md:342`
- **Категория:** other
- **Цитата:** "`/// <inheritdoc/>` comments on methods that no longer implement interfaces (e.g., `RevitPathResolver`, `RevitProcessTracker`) are stale but harmless — replace with proper `<summary>` when editing nearby"

---

## README.md (README-###)

### README-001 — Документация (5 связанных документов, включая CLAUDE.md и README.TOKEN.md)
- **Файл:** `README.md:7-14`
- **Категория:** other
- **Цитата:** Таблица ссылок: ROADMAP.md, Docs/execution-algorithm.md, Docs/qodana-setup.md, AGENTS.md, CLAUDE.md, README.TOKEN.md. (NB: CLAUDE.md и README.TOKEN.md в репо отсутствуют — см. секцию «ДУБЛИ И ПРОТИВОРЕЧИЯ».)

### README-002 — Telegram-бот: навигация, сессии, BIM-команды
- **Файл:** `README.md:3`
- **Категория:** other
- **Цитата:** "Telegram-бот для навигации по файловой системе и управления сессиями экспорта/автоматизации с системой запроса доступа и ролями (User/Admin). Задачи выполняются асинхронно через отдельный Worker-процесс с PostgreSQL-очередью и минутным polling."

### README-003 — .NET 10 long-polling
- **Файл:** `README.md:20`
- **Категория:** other
- **Цитата:** ".NET 10 background service — Telegram-бот с long-polling (webhook-ов нет)."

### README-004 — Telegram.Bot 22.10.0.1
- **Файл:** `README.md:33`
- **Категория:** dependency
- **Цитата:** "**Telegram.Bot 22.10.0.1** — клиент Telegram Bot API"

### README-005 — Npgsql + Dapper 2.1.79
- **Файл:** `README.md:34`
- **Категория:** dependency
- **Цитата:** "**PostgreSQL** — хранение данных (Npgsql + Dapper 2.1.79)"

### README-006 — Worker poll 1 мин
- **Файл:** `README.md:35`
- **Категория:** other
- **Цитата:** "**PostgreSQL queue + polling** — Worker забирает pending-команды из БД раз в минуту"

### README-007 — Serilog Console + Seq
- **Файл:** `README.md:36`
- **Категория:** dependency
- **Цитата:** "**Serilog** — структурированное логирование (Console + Seq)"

### README-008 — OpenMcdf для OLE-потоков
- **Файл:** `README.md:37`
- **Категория:** dependency
- **Цитата:** "**OpenMcdf** — чтение OLE-потоков .rvt/.rfa-файлов (определение версии Revit)"

### README-009 — Windows Registry + Microsoft.Win32
- **Файл:** `README.md:38`
- **Категория:** dependency
- **Цитата:** "**Windows Registry (Microsoft.Win32)** — поиск установленных Revit/Navisworks"

### README-010 — Qodana статический анализ
- **Файл:** `README.md:43`
- **Категория:** other
- **Цитата:** "**Qodana** — статический анализ и поиск мертвого кода. Подробности в [Docs/qodana-setup.md](Docs/qodana-setup.md)."

### README-011 — Windows only + BimLib Windows API
- **Файл:** `README.md:47-49`
- **Категория:** other
- **Цитата:** "⚠️ **Windows only** — проект использует Windows-specific API: Навигация по файловой системе (локальные пути, проверка `RuntimeInformation.IsOSPlatform` в `Program.cs`); **BimLib**: Windows Registry (`Microsoft.Win32`) для поиска Revit.exe/Navisworks.exe; P/Invoke WinAPI (`User32`) для мониторинга процессов и закрытия диалогов"

### README-012 — PostgreSQL 15+
- **Файл:** `README.md:55`
- **Категория:** dependency
- **Цитата:** "**PostgreSQL 15+** — доступный по сети для Server и всех Worker-ов"

### README-013 — 4 проекта, .slnx, BimLib в Worker
- **Файл:** `README.md:61`
- **Категория:** architecture
- **Цитата:** "Решение состоит из **4 проектов** (solution file: `TelegramBot.slnx`). BimLib — не отдельный проект, а директория внутри Worker (`TelegramBot.Worker/BimLib/`)."

### README-014 — Диаграмма проектов (та же что AGENTS.md)
- **Файл:** `README.md:64-70`
- **Категория:** architecture
- **Цитата:** Идентична AGENTS.md:2-16 (TelegramBot.Core ← TelegramBot.Data → Server, Worker + BimLib).

### README-015 — Зависимости проектов
- **Файл:** `README.md:74-77`
- **Категория:** architecture
- **Цитата:** "| `TelegramBot.Core` | Модели, DTO, интерфейсы, конфигурация, константы | Нет (без Telegram SDK) | | `TelegramBot.Data` | PostgreSQL persistence через Dapper + Npgsql | Core | | `TelegramBot.Server` | Telegram инфраструктура, сервисы, хендлеры, хостинг, helpers | Core + Data | | `TelegramBot.Worker` | Фоновое выполнение задач (Revit, Navisworks, AI) + BimLib | Core + Data |"

### README-016 — Полное дерево проекта
- **Файл:** `README.md:81-133`
- **Категория:** architecture
- **Цитата:** Дерево TelegramBot/ (TelegramBot.slnx, TelegramBot.Core/{Config,Constants,DTOs,Extensions,Interfaces,Models}, TelegramBot.Data/{DatabaseInitializer.cs, PostgresDataService.cs, Sql/{Queries.Schema.cs, Queries.Users.cs, Queries.Sessions.cs, Queries.Commands.cs}}, TelegramBot.Server/{Config,Constants,Extensions,Helpers,Interfaces,Properties,Services/{Application,Infrastructure/{FileSystem,Telegram}},Program.cs,appsettings.json}, TelegramBot.Worker/{BimLib/{Config,Interfaces,Models,Monitor,Native,Services}, Services/{CommandExecutionService.cs, BimLibLogFilter.cs}, Program.cs, appsettings.json}, Docs/, scripts/, Dockerfile, .editorconfig, README.md)

### README-017 — Поток обработки запроса (Server)
- **Файл:** `README.md:140-155`
- **Категория:** architecture
- **Цитата:**
  ```
  Telegram API
       ↓
  TelegramBotHostedService (polling, BackgroundService)
       ↓
  TelegramUpdateMapper (Update → MessageDto | CallbackQueryDto)
       ↓
  CommandAppService
       ├── HandleUserCommandAsync (текстовые команды)
       │    ├── /start, /help → SlashCommandService (без проверки доступа)
       │    └── /export, /automation, /status → SlashCommandService (требуется Approved)
       └── HandleCallbackAsync (inline-клавиатуры)
            ↓
       CallbackDispatcher (Chain of Responsibility)
            ↓
       ICallbackHandler (первый подходящий по приоритету)
  ```

### README-018 — Поток выполнения задач
- **Файл:** `README.md:158-183`
- **Категория:** architecture
- **Цитата:** Схема Server (INSERT INTO Commands Status='pending') → PostgreSQL → Worker №1..№N (poll 1 мин, SELECT ... WHERE Status='pending') → PDF/DWG через Revit.exe, NWC/CLASHREP через Navisworks.exe → UPDATE Status='Done'/'Failed' → PostgreSQL.

### README-019 — Worker auto-cleanup по CompletedSessionRetentionDays
- **Файл:** `README.md:185`
- **Категория:** other
- **Цитата:** "Worker автоматически продолжает обработку через polling раз в минуту и скрывает старые неактивные сессии по `Worker:CompletedSessionRetentionDays`."

### README-020 — BimLib: структура, namespaces
- **Файл:** `README.md:189-219`
- **Категория:** architecture / namespace / class-name
- **Цитата:** Те же 6 папок (Config, Interfaces, Models, Monitor, Native, Services) и 6 namespaces (`TelegramBot.BimLib.*`) — см. AGENTS-026..037.

### README-021 — BimLib DI-регистрация (6 строк)
- **Файл:** `README.md:204-210`
- **Категория:** architecture
- **Цитата:** Идентична AGENTS-035: 6 строк AddSingleton в Worker/Program.cs (IRevitVersionDetector→RevitVersionDetector, RevitPathResolver, RevitProcessTracker, DialogDismisser, INavisworksPathResolver→NavisworksPathResolver, NavisworksProcessTracker).

### README-022 — BimIntegration секция в appsettings.json Worker
- **Файл:** `README.md:211`
- **Категория:** config-key
- **Цитата:** "Для работы требуется секция `BimIntegration` в `appsettings.json` Worker-а"

### README-023 — RevitProcessStatus 3 значения
- **Файл:** `README.md:226`
- **Категория:** other
- **Цитата:** "`RevitProcessStatus` содержит только 3 значения: `Healthy`, `NotResponding`, `Error`."

### README-024 — Удалены интерфейсы BimLib
- **Файл:** `README.md:227`
- **Категория:** class-name
- **Цитата:** "Удалены интерфейсы, не имевшие потребителей вне BimLib: `IRevitPathResolver`, `IRevitProcessTracker`, `INavisworksProcessTracker`."

### README-025 — Все сервисы — Singleton
- **Файл:** `README.md:231`
- **Категория:** other
- **Цитата:** "Все сервисы регистрируются как **Singleton** в `DependencyInjectionExtensions.cs` (Server) или напрямую в `Program.cs` (Worker)."

### README-026 — Таблица ключевых сервисов Server
- **Файл:** `README.md:236-246`
- **Категория:** class-name
- **Цитата:** TelegramBotHostedService (Server/Services/Infrastructure/Telegram, polling, очистка), CommandAppService (Server/Services/Application, проверка доступа), SlashCommandService (Server/Services/Application, /export /automation /status /start /help), CallbackDispatcher (Server/Services/Application, Chain-of-responsibility), SessionManager (Server/Services/Application, in-memory ConcurrentDictionary, 5 мин timeout, автоочистка), FileSystemBrowser (Server/Services/Infrastructure/FileSystem), KeyboardBuilder (Server/Services/Infrastructure/Telegram), TelegramOutputService (Server/Services/Infrastructure/Telegram, retry 429), TelegramUpdateMapper (Server/Services/Infrastructure/Telegram), PostgresDataService (TelegramBot.Data, Dapper+Npgsql).

### README-027 — Таблица BimLib-сервисов
- **Файл:** `README.md:251-257`
- **Категория:** class-name
- **Цитата:** RevitVersionDetector, RevitPathResolver, NavisworksPathResolver (Worker/BimLib/Services); RevitProcessTracker, NavisworksProcessTracker (Worker/BimLib/Monitor); DialogDismisser (Worker/BimLib/Monitor).

### README-028 — Таблица Worker-сервисов
- **Файл:** `README.md:262-264`
- **Категория:** class-name
- **Цитата:** CommandExecutionService (TelegramBot.Worker/Services: polling pending, Revit/Navisworks/AI, in-memory batch-счётчик + DB finality, мониторинг здоровья, timeout/lease/crash recovery, автоочистка неактивных сессий, Graceful shutdown для внешних процессов не нужен); BimLibLogFilter (TelegramBot.Worker/Services, отдельный файл для BIM-специфичных логов).

### README-029 — Callback handlers + приоритеты + префиксы
- **Файл:** `README.md:268-277`
- **Категория:** callback-prefix
- **Цитата:**
  | Handler | Priority | Префиксы |
  |---------|----------|----------|
  | `AccessRequestHandler` | 0 | `REQACCESS:`, `APPROVEUSER:`, `REJECTUSER:` |
  | `FileNavigationHandler` | 10 | `GOTOPARENT:` |
  | `FileSelectionHandler` | 20 | `FILE:` |
  | `CommandToggleHandler` | 100 | `PDF:`, `DWG:`, `NWC:`, `IFC:`, `BIMDOC:`, `CLASHREP:`, `AUTORES:` |
  | `SessionManagementHandler` | 100 | `SESSIONDETAILS:`, `DELETESESSION:`, `DELETECOMMAND:`, `CONFIRMDELETESESSION:`, `CONFIRMDELETECOMMAND:` |
  | `CommandSelectionHandler` | 100 | `APPLYCOMMANDS:`, `CANCELCOMMANDSSEL:` |

### README-030 — DB: InitializeDatabaseAsync
- **Файл:** `README.md:281`
- **Категория:** other
- **Цитата:** "PostgreSQL-сервер, доступный по сети. Инициализация таблиц при старте через `host.InitializeDatabaseAsync()`."

### README-031 — Таблицы БД (BotUsers, Sessions, Commands)
- **Файл:** `README.md:285-290`
- **Категория:** sql-table / sql-field
- **Цитата:**
  | Таблица | Поля |
  |---------|------|
  | `BotUsers` | `UserId` (PK), `Username`, `Role` (User/Admin), `Status` (Pending/Approved/Rejected/Blocked), `CreatedAt`, `UpdatedAt` |
  | `Sessions` | `SessionId` (PK, SERIAL), `UserId`, `Username`, `Status` (pending/done/Deleted), `FilesAmount`, `CreatedAt`, `UpdatedAt` |
  | `Commands` | `CommandId` (PK, SERIAL), `SessionId` (FK → Sessions), `CommandText`, `FilePath`, `ExecutionOrder`, `Status` (pending/processing/Done/Failed/Deleted), `GUID`, `Lease`, `Priority`, `RetryCount`, `NextRetryAt` |

  Soft-delete — строки никогда не удаляются физически (статус `Deleted`).

### README-032 — Soft-delete через ⛔ Отменить
- **Файл:** `README.md:299`
- **Категория:** other
- **Цитата:** "**Отмена/удаление команд** — пользователь через `/status` → кнопку «⛔ Отменить» или «🗑»; Server сначала показывает подтверждение, затем мягко удаляет команду (`Status = 'Deleted'`). Worker не выбирает удалённые команды, а `UpdateStatus` не перезаписывает `Deleted`."

### README-033 — command_completed NOTIFY + summary
- **Файл:** `README.md:300`
- **Категория:** other
- **Цитата:** "**Уведомление о завершении** — после завершения всей сессии Worker шлёт `command_completed` через PostgreSQL `NOTIFY`, а Server отправляет пользователю сводку с длительностью сессии и списком ошибочных файлов."

### README-034 — Несколько Worker-ов (competing consumers)
- **Файл:** `README.md:302`
- **Категория:** architecture
- **Цитата:** "Несколько Worker-ов могут работать параллельно (competing consumers) — каждый берёт следующую команду из очереди."

### README-035 — Команды бота (5 команд)
- **Файл:** `README.md:306-314`
- **Категория:** command-code
- **Цитата:** BotCommandsSetup.ConfigureAsync() регистрирует: /start (регистрация, запрос доступа), /export (выбор команд экспорта), /automation (меню команд автоматизации), /status (глобальный просмотр всех сессий с [username], управление), /help (справка).

### README-036 — Базовый флоу работы
- **Файл:** `README.md:318-329`
- **Категория:** other
- **Цитата:** /start → регистрация → /export → APPLYCOMMANDS → выбор .rvt → APPLYFILES → дневной лимит → сессия+команды в БД → Worker → /status → SESSIONDETAILS → DELETECOMMAND/DELETESESSION → ⛔ Отменить → DELETECOMMAND → soft-delete.

### README-037 — Команды экспорта/автоматизации
- **Файл:** `README.md:332`
- **Категория:** command-code
- **Цитата:** "Аналогичный флоу для `/automation` (BIMDOC/CLASHREP/AUTORES)."

### README-038 — Server appsettings.json (пример)
- **Файл:** `README.md:339-362`
- **Категория:** config-key
- **Цитата:** JSON с Serilog (Console+Seq), ConnectionStrings:Postgres, RateLimit{MaxRequests:30, WindowSeconds:60, MaxFilesPerUserPerDay:100}, FileSystem{RvtDirectoryName:"01_RVT", ProjectDirectoryName:"01_PROJECT", RevitFileExtension:".rvt", SectionFolderPattern:"^(\\d{2}|\\d{3}|I{1,3})_"}.

### README-039 — Worker appsettings.json (пример)
- **Файл:** `README.md:365-380`
- **Категория:** config-key
- **Цитата:** JSON с ConnectionStrings:Postgres, BimIntegration{MinSupportedVersion:2018, MaxSupportedVersion:2026, RevitInstallRoot:"C:\\Program Files\\Autodesk"}, Worker{ProcessTimeoutSeconds:10800, CompletedSessionRetentionDays:30}.

### README-040 — appsettings.Local.json пример
- **Файл:** `README.md:383-392`
- **Категория:** config-key
- **Цитата:** JSON с TelegramBot{Token,AdminUserIds:[123456789]} и FileSystem{RootPath:"B:\\"}.

### README-041 — Полная таблица параметров конфигурации
- **Файл:** `README.md:396-415`
- **Категория:** config-key
- **Цитата:**
  - `TelegramBot:Token` → `TelegramBot__Token` (обязательно, валидируется)
  - `TelegramBot:AdminUserIds:0` → `TelegramBot__AdminUserIds__0`
  - `FileSystem:RootPath` → `FileSystem__RootPath` (обязательно, валидируется)
  - `FileSystem:RvtDirectoryName` (default `01_RVT`)
  - `FileSystem:ProjectDirectoryName` (default `01_PROJECT`)
  - `FileSystem:RevitFileExtension` (default `.rvt`)
  - `FileSystem:SectionFolderPattern` (regex)
  - `ConnectionStrings:Postgres` → `ConnectionStrings__Postgres` (default `Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres`)
  - `RateLimit:MaxRequests`, `RateLimit:WindowSeconds`, `RateLimit:MaxFilesPerUserPerDay` (`0` отключает)
  - `Worker:ProcessTimeoutSeconds`
  - `Worker:CompletedSessionRetentionDays` (`0` отключает автоочистку)
  - `BimIntegration:MinSupportedVersion` (default 2018)
  - `BimIntegration:MaxSupportedVersion` (default 2026)
  - `BimIntegration:RevitInstallRoot` (default `C:\Program Files\Autodesk`)

### README-042 — Security: Approved/Blocked/Rejected, админы
- **Файл:** `README.md:423-428`
- **Категория:** other
- **Цитата:** Доступ через /start → Pending → одобрение (APPROVEUSER:); токен в appsettings.Local.json или env; Blocked/Rejected не используют бот; админы (AdminUserIds) автоматически Approved при первом запуске; все одобренные пользователи видят в /status все сессии.

### README-043 — Docker postgres:17
- **Файл:** `README.md:435-441`
- **Категория:** other
- **Цитата:** `docker run -d --name telegram-bot-db -e POSTGRES_DB=telegram_bot -e POSTGRES_PASSWORD=postgres -p 5432:5432 postgres:17`

### README-044 — Windows-контейнеры, Dockerfile
- **Файл:** `README.md:445-450`
- **Категория:** other
- **Цитата:** "Windows-контейнеры (nanoserver ltsc2022). Сборка через многостадийный Dockerfile: `docker build -t telegram-bot-server -f Dockerfile .`; `docker run --rm telegram-bot-server`"

---

## ROADMAP.md (ROADMAP-###)

### ROADMAP-001 — Дата актуальности
- **Файл:** `ROADMAP.md:3`
- **Категория:** other
- **Цитата:** "> Актуально на: 8 июня 2026"

### ROADMAP-002 — v1.0 Core: 4 проекта, DI Singleton
- **Файл:** `ROADMAP.md:9-16`
- **Категория:** architecture
- **Цитата:** "Модели, DTO, интерфейсы, конфигурация — нулевая зависимость от Telegram SDK"; "Архитектура с 4 проектами: `Core → Data → Server`, `Worker`"; "DI-регистрация всех сервисов как Singleton"; "PostgreSQL persistence через Dapper + Npgsql"; "Soft-delete для всех сущностей".

### ROADMAP-003 — v1.0 Server: Long-polling, 7 callback-хендлеров
- **Файл:** `ROADMAP.md:18-28`
- **Категория:** other
- **Цитата:** "Long-polling через `TelegramBotHostedService` (BackgroundService)"; "Обработка текстовых команд: `/start`, `/help`, `/export`, `/automation`, `/status`"; "Chain of Responsibility для callback-хендлеров (7 хендлеров)"; "Команды экспорта: PDF, DWG, NWC, IFC"; "Команды автоматизации: BIMDOC, CLASHREP, AUTORES".

### ROADMAP-004 — v1.0 Worker: poll 1 мин, lease, FOR UPDATE SKIP LOCKED
- **Файл:** `ROADMAP.md:30-41`
- **Категория:** architecture
- **Цитата:** "Polling очереди команд раз в 1 минуту"; "Пул процессов (глобальный SemaphoreSlim)"; "Lease-механизм (TTL)"; "FOR UPDATE SKIP LOCKED — конкурентная обработка несколькими воркерами"; "Graceful shutdown исключён из требований"; "Приоритеты команд (`Priority ASC, CreatedAt ASC, CommandId ASC`)"; "Поля `StartedAt`, `CompletedAt`, `ProcessId`, `ErrorMessage`".

### ROADMAP-005 — v1.0 Data: 5 partial-файлов SQL
- **Файл:** `ROADMAP.md:43-47`
- **Категория:** other
- **Цитата:** "SQL-запросы, разбитые по сущностям (5 partial-файлов)" — **NB:** AGENTS.md:19/210 утверждает 4 partial-файла. См. секцию «ПРОТИВОРЕЧИЯ».

### ROADMAP-006 — v1.1 Lease TTL = ProcessTimeoutSeconds + 5 мин
- **Файл:** `ROADMAP.md:53`
- **Категория:** other
- **Цитата:** "**Lease с долгим TTL** — при захвате команды Lease = ProcessTimeoutSeconds + 5 мин. Команда не вернётся в очередь раньше ProcessTimeout. Фоновая очистка каждые 60 сек возвращает команды с истёкшим Lease."

### ROADMAP-007 — v1.1 Async stdout/stderr
- **Файл:** `ROADMAP.md:55-58`
- **Категория:** other
- **Цитата:** "**Асинхронное чтение stdout/stderr** — `BeginOutputReadLine` / `BeginErrorReadLine`. Вывод собирается в `StringBuilder` через событийные хендлеры. Больше нет deadlock при заполнении буфера 64KB. Логируется: stdout → Information, stderr → Warning. Обрезка >4KB для защиты от раздувания логов."

### ROADMAP-008 — v1.1 FilePath validation + AllowedExtensions
- **Файл:** `ROADMAP.md:60`
- **Категория:** other
- **Цитата:** "**Валидация FilePath** — проверка существования файла, расширения (из `AllowedExtensions`), защита от path traversal (`Path.GetFullPath()`)."

### ROADMAP-009 — v1.1 Priority partitions (Critical/High/Medium/Low/Lowest)
- **Файл:** `ROADMAP.md:62-71`
- **Категория:** other
- **Цитата:** "**Приоритетные партиции (priority-based)** — `SortedDictionary<int, SemaphoreSlim>`: Critical (1) → 3, High (2) → 5, Medium (3) → 3, Low (4) → 1, Lowest (5+) → 1. Маршрутизация: первый partition threshold `>= Priority`, иначе последний threshold. Пороги по возрастанию: thresholds `[1, 2, 3, 4, 5]`. Чем меньше Priority, тем выше приоритет (1 = Critical, 5 = Lowest)."

### ROADMAP-010 — v1.1 Retry logic
- **Файл:** `ROADMAP.md:72-73`
- **Категория:** other
- **Цитата:** "**Retry logic** — экспоненциальная задержка (`base * 2^(attempt-1)`): 60s, 120s, 240s, ... Лимит попыток: `MaxRetries=5`. Команда возвращается в `pending` с `NextRetryAt`."

### ROADMAP-011 — v1.1 command_completed LISTEN/NOTIFY
- **Файл:** `ROADMAP.md:74-77`
- **Категория:** other
- **Цитата:** "**Telegram-уведомления** — о завершении/ошибках команд через отдельный канал LISTEN/NOTIFY (`command_completed`). `CommandNotificationService` слушает и отправляет сообщения. Уведомления приходят только при завершении всей сессии (сводка: `N ✅, M ❌`), с указанием имени проекта и списком файлов с ошибками."

### ROADMAP-012 — v1.1 Lease cleanup coordination pg_try_advisory_lock
- **Файл:** `ROADMAP.md:78-79`
- **Категория:** other
- **Цитата:** "**Координация очистки Lease** — `pg_try_advisory_lock(1234567)` перед каждой очисткой. Только один воркер выполняет очистку, остальные пропускают цикл."

### ROADMAP-013 — v1.1 Primary constructors migration
- **Файл:** `ROADMAP.md:80-81`
- **Категория:** other
- **Цитата:** "**Primary constructors** — миграция сервисов на C# 12 (TelegramBotHostedService, CallbackDispatcher, CommandExecutionService, CommandNotificationService и др.)"

### ROADMAP-014 — v1.1 PathMap удалён
- **Файл:** `ROADMAP.md:82-84`
- **Категория:** other
- **Цитата:** "**Рефакторинг навигации** — удалён `PathMap`/`TryResolvePath`, передача путей напрямую в callback-данных вместо токенов. Упрощение `FileSystemBrowser`, `FileNavigationHandler`, `FileSelectionHandler`."

### ROADMAP-015 — v1.2 BimLib встроен в Worker
- **Файл:** `ROADMAP.md:91-95`
- **Категория:** architecture
- **Цитата:** "**BimLib (встроен в Worker)** — библиотека для определения версии Revit, резолвинга Revit.exe и мониторинга процессов. [x] **Определение версии Revit по .rvt-файлу**: чтение OLE-потока BasicFileInfo через OpenMcdf, поиск строки `Format: YYYY`. [x] **Автоматический выбор Revit.exe**: поиск пути через реестр Windows (`HKLM\\SOFTWARE\\Autodesk\\Revit\\{version}`) с fallback на WOW6432Node. [x] **Мониторинг здоровья процесса**: проверка отклика, автозакрытие диалогов Revit. [x] **Поддержка Navisworks**: поиск Navisworks.exe/FileConvert.exe через реестр Windows, мониторинг процессов (Roamer, FileConvert)."

### ROADMAP-016 — v1.2 Graceful shutdown не нужен
- **Файл:** `ROADMAP.md:96-97`
- **Категория:** other
- **Цитата:** "**Graceful shutdown не нужен** — при остановке Worker не реализует отдельное ожидание или завершение Revit/Navisworks. Корректность обеспечивают timeout, lease/crash recovery и повторный захват команд после перезапуска."

### ROADMAP-017 — v1.2 BimLibLogFilter, отдельный файл логов
- **Файл:** `ROADMAP.md:98-100`
- **Категория:** other
- **Цитата:** "**Расширенное логирование Revit-специфичных ошибок** — отдельный файл BimLib.log (`~/Documents/TelegramBot/Logs/Worker/BimLib/log-.txt`), фильтрация через BimLibLogFilter по SourceContext \"TelegramBot.BimLib.*\""

### ROADMAP-018 — v1.2 Rate limiting (sliding window per-user)
- **Файл:** `ROADMAP.md:101-102`
- **Категория:** other
- **Цитата:** "**Rate limiting** — ограничение на количество команд от одного пользователя в единицу времени (sliding window per-user)."

### ROADMAP-019 — v1.2 ProjectName TEXT в Sessions
- **Файл:** `ROADMAP.md:103-104`
- **Категория:** sql-field
- **Цитата:** "**ProjectName в БД** — колонка `ProjectName TEXT` в таблице `Sessions`. Имя проекта отображается в `/status` и в уведомлениях о завершении."

### ROADMAP-020 — v1.2 Список ошибочных файлов
- **Файл:** `ROADMAP.md:105-106`
- **Категория:** other
- **Цитата:** "**Список ошибочных файлов в уведомлении** — при наличии ошибок уведомление содержит список файлов с ошибками: `\\n\\nОшибки:\\n- file.rvt`."

### ROADMAP-021 — v1.2 Timing stats MIN(StartedAt)/MAX(CompletedAt)
- **Файл:** `ROADMAP.md:107-108`
- **Категория:** other
- **Цитата:** "**Timing stats в уведомлениях** — уведомление о завершении содержит длительность сессии, рассчитанную по `MIN(StartedAt)` / `MAX(CompletedAt)` из таблицы `Commands`."

### ROADMAP-022 — v1.2 In-memory счётчик сессий
- **Файл:** `ROADMAP.md:109-111`
- **Категория:** other
- **Цитата:** "**Оптимизация: in-memory счётчик сессий** — удалён per-command `GetSessionProgressAsync`, заменён на `ConcurrentDictionary.AddOrUpdate`. Счётчик используется как batch-local оптимизация, а финальность сессии подтверждается БД через отсутствие `pending`/`processing`."

### ROADMAP-023 — v1.2 Retry/counter bug fix
- **Файл:** `ROADMAP.md:112-113`
- **Категория:** other
- **Цитата:** "**Исправлен retry/counter bug** — retry теперь завершает текущий claim и декрементит `_sessionRemaining`; уведомление не теряется после повторных попыток."

### ROADMAP-024 — v1.2 MaxFilesPerUserPerDay
- **Файл:** `ROADMAP.md:114-115`
- **Категория:** config-key
- **Цитата:** "**Дневной лимит файлов на пользователя** — `RateLimit:MaxFilesPerUserPerDay` ограничивает количество файлов, которые пользователь может поставить в очередь за 24 часа (`0` отключает лимит)."

### ROADMAP-025 — v1.2 Автоочистка старых сессий
- **Файл:** `ROADMAP.md:116-117`
- **Категория:** other
- **Цитата:** "**Автоочистка старых сессий** — Worker мягко удаляет неактивные сессии старше `Worker:CompletedSessionRetentionDays`, если в них нет `pending`/`processing` команд (`0` отключает)."

### ROADMAP-026 — v1.2 Confirmation dialogs
- **Файл:** `ROADMAP.md:118-119`
- **Категория:** callback-prefix
- **Цитата:** "**Confirmation dialogs для удаления** — кнопки удаления сессии/команды сначала показывают подтверждение через `CONFIRMDELETESESSION:` / `CONFIRMDELETECOMMAND:`."

### ROADMAP-027 — v1.2 Удалён мёртвый код
- **Файл:** `ROADMAP.md:120-121`
- **Категория:** other
- **Цитата:** "**Удалён мёртвый код** — `GetCommandStatusAsync` (interface + implementation + SQL), `GetFailedFilesBySession` (не использовался — inline SQL вместо константы)."

### ROADMAP-028 — v1.2 В планах: Revit Journal, Prometheus, статистика
- **Файл:** `ROADMAP.md:123-129`
- **Категория:** other
- **Цитата:** "Опционально: Revit Journal-автоматизация"; "Интеграция Prometheus/Grafana — метрики: количество активных команд, время выполнения, количество ошибок по типам, размер очереди. Exporter в `CommandExecutionService` и `CommandNotificationService`"; "Статистика выполнения — среднее время выполнения, процент успеха/ошибок".

### ROADMAP-029 — Открытые вопросы (6)
- **Файл:** `ROADMAP.md:131-143`
- **Категория:** other
- **Цитата:** Prometheus/Grafana (HTTP exporter vs SQL); статистика (агрегаты vs on-demand); Revit Journal (сценарии); статус Sessions (Done/Failed vs computed); фильтрация /status (проект/пользователь/статус/пагинация); Pre-warm Revit (idle пул).

### ROADMAP-030 — Не планируется: Health checks, new_command LISTEN/NOTIFY
- **Файл:** `ROADMAP.md:146-148`
- **Категория:** other
- **Цитата:** "**Health checks для Worker** — удалено из roadmap: сейчас не используется Docker/K8s, поэтому отдельные `/health`, `/healthz`, `/readyz` не нужны."; "**`new_command LISTEN/NOTIFY` для Worker** — по текущему решению не требуется; Worker использует polling очереди раз в минуту. `LISTEN/NOTIFY` остаётся только для `command_completed` уведомлений Server-а."

### ROADMAP-031 — v1.3 Упрощение алгоритма (планы)
- **Файл:** `ROADMAP.md:151-173`
- **Категория:** architecture
- **Цитата:** Разделы: «Упрощение алгоритма выполнения» (Исправить критические ошибки, Единая модель жизненного цикла команды `pending → processing → done/failed/deleted`, Свести retry/lease/timeout к одному сценарию, Упростить уведомления о завершении сессии; уже отмечены: Пересмотреть in-memory счётчик сессий ✅, Синхронизировать модель очереди с кодом ✅) + «Упрощение кодовой базы» (Разделить CommandExecutionService, Свести SQL к сценарным методам, Удалить мёртвые/исторические ветки, Упростить callback-хендлеры статуса и удаления, Синхронизировать документацию).

### ROADMAP-032 — v1.2 Рефакторинг (завершено)
- **Файл:** `ROADMAP.md:195-210`
- **Категория:** other
- **Цитата:** Таблица завершённых пунктов: DB-трекинг сообщений сохранён ✅, Удалены лишние интерфейсы (`IFileSystemBrowser`, `ITelegramUpdateMapper`, `IRevitPathResolver`, `IRevitProcessTracker`, `INavisworksProcessTracker`) ✅, Primary constructors — удалены ~23 redundant поля из 8 классов ✅, CallbackHandlerBase — убрано двойное логирование ✅, Unused usings (dotnet format --diagnostics IDE0005) ✅, Унификация дубликатов (HandlerHelpers, ProcessHealthHelper, NpgsqlHelper, TryParseId) ✅, Упрощение DI (TelegramOutputService без IDataService, убраны 2 лишних параметра из TelegramBotHostedService, мёртвый IDataService убран из CommandAppService) ✅, PostgresDataService.CreateConnectionAsync() ✅, Документация обновлена ✅.

### ROADMAP-033 — TryParseId унификация 5 блоков int.TryParse
- **Файл:** `ROADMAP.md:207`
- **Категория:** class-name
- **Цитата:** "`TryParseId()` | Заменяет 5 одинаковых блоков `int.TryParse` в `SessionManagementHandler`"

### ROADMAP-034 — NpgsqlHelper перенесён Worker.Services → TelegramBot.Data
- **Файл:** `ROADMAP.md:205-206`
- **Категория:** class-name
- **Цитата:** "`NpgsqlHelper.CreateOpenConnectionAsync()` | Перенесён из `Worker.Services` (internal) → `TelegramBot.Data` (public). Используется в `CommandExecutionService` (Worker) и `CommandNotificationService` (Server)"

### ROADMAP-035 — Рекомендации по улучшению
- **Файл:** `ROADMAP.md:218-226`
- **Категория:** other
- **Цитата:** Таблица: 1) Pre-warm Revit 🔥, 2) Timing stats ✅ Реализовано, 3) Фильтрация в /status 🟠, 4) Дневной лимит файлов ✅, 5) Автоочистка старых сессий ✅, 6) Умный retry (transient vs permanent) 🟡, 7) Confirmation dialogs ✅.

### ROADMAP-036 — Шкала приоритетов и статусов
- **Файл:** `ROADMAP.md:229-248`
- **Категория:** other
- **Цитата:** 🔥 Высокий, 🟠 Средний, 🟡 Низкий. ✅ v1.0, 🟢 v1.1/v1.2, 🟡 v1.3, ⚪ v2.0+, 🔄 в работе.

### ROADMAP-037 — Связанные документы
- **Файл:** `ROADMAP.md:252-259`
- **Категория:** other
- **Цитата:** "Docs/execution-algorithm.md, README.md, AGENTS.md, CLAUDE.md, Docs/qodana-setup.md, README.TOKEN.md" — **NB:** CLAUDE.md и README.TOKEN.md в репо отсутствуют.

---

## Docs/execution-algorithm.md (DOCS-EXEC-###)

### DOCS-EXEC-001 — Архитектурные паттерны
- **Файл:** `Docs/execution-algorithm.md:29-43`
- **Категория:** architecture
- **Цитата:** "Chain of Responsibility (CallbackDispatcher), Strategy (CommandConfig), Competing Consumers (FOR UPDATE SKIP LOCKED), Polling (Task.Delay), Bulkhead (Priority-based партиции, SemaphoreSlim), Recovery loop, Retry with Exponential Backoff (`MaxRetries=5`, 60s → 120s → 240s → 480s → 960s), Lease, Soft Delete, Singleton."

### DOCS-EXEC-002 — Ключевые концепции
- **Файл:** `Docs/execution-algorithm.md:48-56`
- **Категория:** architecture
- **Цитата:** "Пул процессов, Lease-механизм, Таймауты, Приоритеты, Партиции, Отмена команд (любой одобренный пользователь, soft-delete, все одобренные могут удалять чужие сессии)."

### DOCS-EXEC-003 — Партиции по умолчанию
- **Файл:** `Docs/execution-algorithm.md:120-125`
- **Категория:** other
- **Цитата:** "Priority 1 → Critical, SemaphoreSlim(3); Priority 2 → High, SemaphoreSlim(5); Priority 3 → Medium, SemaphoreSlim(3); Priority 4 → Low, SemaphoreSlim(1); Priority 5+ → Lowest, SemaphoreSlim(1)."

### DOCS-EXEC-004 — BimLib в Worker, не отдельный проект
- **Файл:** `Docs/execution-algorithm.md:138-139`
- **Категория:** architecture
- **Цитата:** "BimLib — **Windows-only** набор модулей, расположенный внутри Worker-проекта (`TelegramBot.Worker/BimLib/`). Используется `CommandExecutionService` при выполнении Revit/Navisworks-команд."

### DOCS-EXEC-005 — BimLib подпапки (Services/Monitor/Native/Interfaces/Models/Config)
- **Файл:** `Docs/execution-algorithm.md:154-174`
- **Категория:** class-name
- **Цитата:** Та же структура что AGENTS.md: Services (RevitVersionDetector, RevitPathResolver, NavisworksPathResolver), Monitor (RevitProcessTracker, NavisworksProcessTracker, DialogDismisser, ProcessHealthHelper, WindowUtil, WindowInfo), Native (P/Invoke User32, Win32Consts), Interfaces (IRevitVersionDetector, INavisworksPathResolver — 2 интерфейса), Models (RevitDetectedVersion, RevitProcessHealth), Config (BimIntegrationOptions).

### DOCS-EXEC-006 — Удалённые интерфейсы BimLib
- **Файл:** `Docs/execution-algorithm.md:178-179`
- **Категория:** class-name
- **Цитата:** "Ранее существовавшие интерфейсы `IRevitPathResolver`, `IRevitProcessTracker`, `INavisworksProcessTracker` удалены — у них не было потребителей вне BimLib."

### DOCS-EXEC-007 — DI регистрация BimLib (6 строк)
- **Файл:** `Docs/execution-algorithm.md:209-216`
- **Категория:** architecture
- **Цитата:** Идентичный блок из 6 AddSingleton-ов в Worker/Program.cs.

### DOCS-EXEC-008 — BimIntegration секция JSON
- **Файл:** `Docs/execution-algorithm.md:218-225`
- **Категория:** config-key
- **Цитата:** `BimIntegration: {MinSupportedVersion: 2018, MaxSupportedVersion: 2026, RevitInstallRoot: "C:\\Program Files\\Autodesk"}`

### DOCS-EXEC-009 — BimLib important notes
- **Файл:** `Docs/execution-algorithm.md:230-234`
- **Категория:** other
- **Цитата:** "BimLib помечена `[SupportedOSPlatform(\"windows\")]` — работает только на Windows"; "OpenMcdf 3.x парсит OLE Structured Storage (.rvt). API: `RootStorage.OpenRead()` → `OpenStream()` → `stream.Read()`"; "Доступ к реестру Windows через `Microsoft.Win32.Registry`"; "P/Invoke — в `Native/User32.cs`"; "`RevitProcessStatus` содержит 3 значения: `Healthy`, `NotResponding`, `Error`"

### DOCS-EXEC-010 — Жизненный цикл команды (5 статусов)
- **Файл:** `Docs/execution-algorithm.md:240-249`
- **Категория:** other
- **Цитата:** "pending, processing, Done, Failed, Deleted." + "Статус `processing` устанавливается атомарно при захвате команды с использованием `SELECT ... FOR UPDATE SKIP LOCKED`. Статус `Deleted` является финальным — Worker не должен перезаписывать мягко удалённую команду."

### DOCS-EXEC-011 — DefaultBatchSize=5
- **Файл:** `Docs/execution-algorithm.md:309`
- **Категория:** other
- **Цитата:** "Захват pending-команд из БД (до DefaultBatchSize=5)"

### DOCS-EXEC-012 — Алгоритм захвата/освобождения partition слота
- **Файл:** `Docs/execution-algorithm.md:366-373`
- **Категория:** architecture
- **Цитата:** "`threshold = GetPartitionThreshold(cmd.Priority)` выбирает первый threshold в [1,2,3,4,5], где threshold >= Priority. Если подходящего threshold нет — fallback: `threshold = _partitionThresholds[^1]` (5). `_partitionPools[threshold].WaitAsync()` блокирует поток, пока слот не освободится. При отмене (CancellationToken) выбрасывает `OperationCanceledException`. Освобождение в `finally` `ProcessWithPoolAsync`."

### DOCS-EXEC-013 — Трекинг активных процессов: ConcurrentDictionary<int, Process>
- **Файл:** `Docs/execution-algorithm.md:383-395`
- **Категория:** architecture
- **Цитата:** "`private readonly ConcurrentDictionary<int, Process> _activeProcesses;` Перед запуском `_activeProcesses[cmd.CommandId] = process`; после завершения (в finally) `_activeProcesses.TryRemove(cmd.CommandId, out _)`. `Process` хранится напрямую, без класса-обёртки. `Stopwatch` и `CommandId` — локальные переменные в `ExecuteOneAsync`."

### DOCS-EXEC-014 — Graceful shutdown не нужен (см. также ROADMAP-016, AGENTS)
- **Файл:** `Docs/execution-algorithm.md:379`
- **Категория:** architecture
- **Цитата:** "**Graceful Shutdown не нужен.** При остановке Worker не должен ждать активные Revit/Navisworks-процессы и не должен пытаться завершать их отдельным shutdown-сценарием."

### DOCS-EXEC-015 — LeaseTimeoutMin=5, CleanupIntervalSec=60
- **Файл:** `Docs/execution-algorithm.md:454-455`
- **Категория:** config-key
- **Цитата:** "`LeaseTimeoutMin = 5` — Lease истекает через 5 минут"; "`CleanupIntervalSec = 60` — проверка каждые 60 секунд" — **NB:** в ROADMAP-006 сказано Lease = ProcessTimeoutSeconds + 5 мин; тут LeaseTimeoutMin=5 мин (фиксированное 5 мин). Возможное противоречие (см. секцию «ПРОТИВОРЕЧИЯ»).

### DOCS-EXEC-016 — SQL захвата (FOR UPDATE SKIP LOCKED) + lease
- **Файл:** `Docs/execution-algorithm.md:516-531`
- **Категория:** sql-table / sql-field
- **Цитата:** "WITH selected AS (SELECT c.CommandId FROM Commands c JOIN Sessions s ON s.SessionId = c.SessionId WHERE c.Status = 'pending' AND s.Status != 'Deleted' ORDER BY Priority ASC, CreatedAt ASC LIMIT @Limit FOR UPDATE SKIP LOCKED) UPDATE Commands c SET Status = 'processing', Lease = @LeaseExpiry FROM selected WHERE c.CommandId = selected.CommandId RETURNING ..."

### DOCS-EXEC-017 — Connection reconnection (5 sec)
- **Файл:** `Docs/execution-algorithm.md:587-606`
- **Категория:** other
- **Цитата:** "Outer retry loop: `await Task.Delay(ReconnectDelayMs, stoppingToken)` при потере соединения. При переподключении: 1) Создаётся новое подключение, 2) Очищаются истёкшие Lease, 3) Цикл продолжается."

### DOCS-EXEC-018 — Валидация FilePath
- **Файл:** `Docs/execution-algorithm.md:574-580`
- **Категория:** other
- **Цитата:** "Путь не пустой; Канонический путь не отличается от исходного (защита от `../` traversal); Файл существует; Расширение файла входит в `AllowedExtensions` (если указаны)"

### DOCS-EXEC-019 — Payload command_completed
- **Файл:** `Docs/execution-algorithm.md:695-706`
- **Категория:** other
- **Цитата:** "NOTIFY command_completed, 'UserId|SessionId|Done|Total|ProjectName' (pipe-разделённые поля, Split('|', 5)). UserId BIGINT, SessionId INT, Done INT, Total INT, ProjectName TEXT (пусто для старых сессий)."

### DOCS-EXEC-020 — In-memory счётчик _sessionRemaining (Worker)
- **Файл:** `Docs/execution-algorithm.md:729-761`
- **Категория:** architecture
- **Цитата:** "`private readonly ConcurrentDictionary<int, int> _sessionRemaining = new();` При ClaimPendingCommandsAsync — добавляем claimed-команды текущего batch-а (`GroupBy(SessionId)`, AddOrUpdate). При выходе захваченной команды из processing — CompleteClaimedCommandAsync: `AddOrUpdate` декрементит. Если `newRemaining == 0` — проверяем `CountPendingProcessingBySessionAsync`. Если 0 — `GetSessionsStatusAsync` + `NotifyCommandCompletedAsync`. Lock-free."

### DOCS-EXEC-021 — Server side: duration + failed files SQL
- **Файл:** `Docs/execution-algorithm.md:768-787`
- **Категория:** sql-table / sql-field
- **Цитата:** "SELECT EXTRACT(EPOCH FROM (MAX(CompletedAt) - MIN(StartedAt)))::int FROM Commands WHERE SessionId = @SessionId AND Status != 'Deleted' AND StartedAt IS NOT NULL AND CompletedAt IS NOT NULL; SELECT FilePath FROM Commands WHERE SessionId = @SessionId AND Status = 'Failed'; имена через `Path.GetFileName()` + добавляются как `\\n\\nОшибки:\\n- model.rvt\\n- another.rvt`"

### DOCS-EXEC-022 — Server side: NOTIFY без markdown
- **Файл:** `Docs/execution-algorithm.md:799-804`
- **Категория:** other
- **Цитата:** "`CommandNotificationService` — `BackgroundService`, подписан на `LISTEN command_completed`. При получении NOTIFY парсит payload через `Split('|', 5)`. Запрашивает длительность сессии по `MIN(StartedAt)` / `MAX(CompletedAt)`. Если `failed > 0` — запрашивает Failed-файлы из БД. Отправляет сводку через `ITelegramOutputService.SendMessageAsync()`. Markdown-форматирование не используется (plain text)."

### DOCS-EXEC-023 — Параметры конфигурации
- **Файл:** `Docs/execution-algorithm.md:812-822`
- **Категория:** config-key
- **Цитата:** "Partitions: `{1→3, 2→5, 3→3, 4→1, 5→1}`; ProcessTimeoutSeconds: 10800 (3 часа); MaxRetries: 5; RetryDelayBaseSeconds: 60; CompletedSessionRetentionDays: 30; CleanupIntervalSec: 60; HealthCheckIntervalSec: 30; FallbackTimeoutSec: 60 (1 мин — polling); ReconnectDelayMs: 5000."

### DOCS-EXEC-024 — Worker:Commands JSON (PDF/DWG/NWC/AUTORES)
- **Файл:** `Docs/execution-algorithm.md:826-866`
- **Категория:** config-key
- **Цитата:** Worker.Commands: PDF (Revit.exe, `/command \"{CommandText}\" \"{FilePath}\"`, .rvt/.rfa); DWG (то же); NWC (FileConvert.exe, .nwc/.nwd/.nwf); AUTORES (python, `ai_agent.py --command \"{CommandText}\" --file \"{FilePath}\"`, .rvt/.ifc/.nwc, WorkingDirectory=".")."

### DOCS-EXEC-025 — CommandPriorityMap (PDF=1, DWG=2, NWC/IFC/BIMDOC/CLASHREP=3, AUTORES=4, fallback=50)
- **Файл:** `Docs/execution-algorithm.md:870-880`
- **Категория:** other
- **Цитата:** "PDF=1, DWG=2, NWC/IFC/BIMDOC/CLASHREP=3, AUTORES=4, Не указана в мапе=50 (Lowest, fallback)."

### DOCS-EXEC-026 — База данных: 4 таблицы
- **Файл:** `Docs/execution-algorithm.md:890-897`
- **Категория:** sql-table
- **Цитата:** "В системе **4 таблицы**: `BotUsers`, `Sessions`, `Commands`, `TrackedMessages`."

### DOCS-EXEC-027 — Команды (поля) — повтор AGENTS / README
- **Файл:** `Docs/execution-algorithm.md:1000-1013`
- **Категория:** sql-field
- **Цитата:** "CommandId, SessionId, CommandText, FilePath, ExecutionOrder, Status, CreatedAt, StartedAt, CompletedAt, Lease (Unix sec), Priority (1–5, default 50, из CommandPriorityMap: PDF=1, DWG=2, NWC/IFC/BIMDOC/CLASHREP=3, AUTORES=4), ProcessId, ErrorMessage."

### DOCS-EXEC-028 — Индексы БД
- **Файл:** `Docs/execution-algorithm.md:1019-1024`
- **Категория:** sql-table
- **Цитата:** "Индексы: `(Status, Priority, CreatedAt)`; `(Status, Lease) WHERE Status = 'processing'`; `(SessionId)`; `(UserId, CreatedAt DESC)`."

### DOCS-EXEC-029 — SQL вставки Commands
- **Файл:** `Docs/execution-algorithm.md:1032-1037`
- **Категория:** sql-table
- **Цитата:** "INSERT INTO \"Commands\" (\"SessionId\", \"CommandText\", \"FilePath\", \"ExecutionOrder\", \"Priority\") SELECT @SessionId, unnest(@CommandTexts::text[]), unnest(@FilePaths::text[]), unnest(@Orders::int[]), unnest(@Priorities::int[]);"

### DOCS-EXEC-030 — SQL захвата (Commands + JOIN Sessions, project name поле)
- **Файл:** `Docs/execution-algorithm.md:1042-1062`
- **Категория:** sql-table / sql-field
- **Цитата:** "SELECT c.CommandId, c.SessionId, c.CommandText, c.FilePath, c.ExecutionOrder, s.UserId, s.Username, **c.Partition, c.Priority** FROM \"Commands\" c JOIN \"Sessions\" s ON s.\"SessionId\" = c.\"SessionId\" WHERE c.\"Status\" = 'pending' AND s.\"Status\" != 'Deleted' ORDER BY c.\"Priority\" ASC, c.\"CreatedAt\" ASC LIMIT @Limit FOR UPDATE SKIP LOCKED; UPDATE \"Commands\" c SET \"Status\" = 'processing', \"Lease\" = @LeaseExpiry, \"StartedAt\" = NOW() FROM selected WHERE c.\"CommandId\" = selected.\"CommandId\" RETURNING ..." — **NB:** упомянуто поле `c.Partition` (не упомянуто в AGENTS/README/ROADMAP). См. «ПРОТИВОРЕЧИЯ».

### DOCS-EXEC-031 — SQL отмены команды пользователем
- **Файл:** `Docs/execution-algorithm.md:1118-1125`
- **Категория:** sql-table
- **Цитата:** "UPDATE Commands SET Status = 'Deleted' WHERE CommandId = @CommandId AND (SessionId IN (SELECT SessionId FROM Sessions WHERE UserId = @UserId) OR @IsAdmin = true);"

### DOCS-EXEC-032 — Безопасность и надёжность (таблица)
- **Файл:** `Docs/execution-algorithm.md:1131-1153`
- **Категория:** other
- **Цитата:** Логическое удаление, FOR UPDATE SKIP LOCKED, SortedDictionary<int,SemaphoreSlim>, ORDER BY Priority ASC, ProcessId+Kill(true), Переподключение 5 сек, Stdout/stderr асинхронно (64KB), NOTIFY command_completed → CommandNotificationService, Автоочистка, Polling queue, Lease = ProcessTimeoutSeconds + 5 мин (долгий TTL), Валидация FilePath, Подтверждение удаления (`Status = 'Deleted'`, UpdateStatus с `WHERE Status != 'Deleted'`).

### DOCS-EXEC-033 — ExecuteOneAsync: 11 шагов
- **Файл:** `Docs/execution-algorithm.md:1159-1178`
- **Категория:** other
- **Цитата:** "1. Валидация FilePath → 2. Поиск конфигурации `WorkerOptions.Commands.TryGetValue(CommandText)` → 3. `CreateProcessStartInfo` (FileName, Arguments (с подстановкой {CommandText}, {FilePath}), WorkingDirectory (null→папка файла, "."→CurrentDirectory), Redirect, UseShellExecute=false, CreateNoWindow=true) → 4. process.Start() → 5. Трекинг → 6. UpdateCommandStatus(Processing, ProcessId) → 7. BeginOutputReadLine/BeginErrorReadLine → 8. WaitForExit → 9. Логирование stdout/stderr (обрезка >4KB) → 10. Результат (Timeout→Kill(true) Failed, ExitCode 0→Done, иначе Failed) → 11. Очистка (partitionPool.Release, _activeProcesses.TryRemove)."

### DOCS-EXEC-034 — CommandConfig поля
- **Файл:** `Docs/execution-algorithm.md:1184-1189`
- **Категория:** other
- **Цитата:** "CommandConfig: `ExecutablePath`, `ArgumentsTemplate`, `AllowedExtensions` (null — любое), `WorkingDirectory` (null — папка файла, \".\" — корень процесса)."

### DOCS-EXEC-035 — Расширение системы (4 шага)
- **Файл:** `Docs/execution-algorithm.md:1222-1267`
- **Категория:** other
- **Цитата:** "1. Добавить запись в Commands (JSON); 2. Настроить приоритет через CommandPriorityMap (иначе Priority=50, Lowest); 3. Опционально — настроить лимиты партиций; 4. Опционально — добавить кнопку в UI."

### DOCS-EXEC-036 — Диагностические SQL запросы
- **Файл:** `Docs/execution-algorithm.md:1271-1369`
- **Категория:** other
- **Цитата:** Запросы для: pending-очереди, активных выполнений, истории 24ч, зависших (Lease, StartedAt > 1 час), статистики, удалённых, pg_listening_channels, мониторинга PID, Get-Process PowerShell.

### DOCS-EXEC-037 — Критерии корректной реализации (11 пунктов)
- **Файл:** `Docs/execution-algorithm.md:1373-1387`
- **Категория:** other
- **Цитата:** 11 критериев: Лимит процессов (Critical=1→3, High=2→5, Medium=3→3, Low=4→1, Lowest=5+→1); Приоритизация; Lease; Таймауты; PID; FOR UPDATE SKIP LOCKED; Graceful shutdown; Polling queue; Восстановление; Наблюдаемость; Отмена команд.

### DOCS-EXEC-038 — Известные ограничения и технический долг (DOC-001..011)
- **Файл:** `Docs/execution-algorithm.md:1391-1415`
- **Категория:** other
- **Цитата:** DOC-001..011 таблица: Lease < ProcessTimeout (✅ Исправлено v1.1), Stdout/stderr deadlock (✅ v1.1), FilePath validation (✅ v1.1), Приоритетные партиции (✅ v1.1), Retry (✅ v1.2), Prometheus (В планах v1.2), Координация очистки Lease pg_try_advisory_lock(1234567) (✅ v1.2), Graceful shutdown (🟢 NONE Зафиксировано), Health checks (🟡 Улучшение), Лимит очереди (🟡 Улучшение), Runbook (🟡 Улучшение)."

---

## Docs/CommandExecutionAlgorithm.md (DOCS-CEA-###)

### DOCS-CEA-001 — Участники (User, Server, App, DB, Worker, Process)
- **Файл:** `Docs/CommandExecutionAlgorithm.md:10-17`
- **Категория:** other
- **Цитата:** "User, Server (`TelegramBotHostedService`), App (`CommandAppService` + `SlashCommandService`), DB (PostgreSQL, LISTEN/NOTIFY), Worker (`CommandExecutionService`), Process (Revit/Navisworks/Python)."

### DOCS-CEA-002 — Создание задачи: ConfirmFileSelectionAsync, CreateSessionWithCommandsAsync
- **Файл:** `Docs/CommandExecutionAlgorithm.md:23-50`
- **Категория:** architecture
- **Цитата:** "User → /export → команды → Confirm → Server → ConfirmFileSelectionAsync() → App → CollectRvtFiles() → DB; CreateSessionWithCommandsAsync() → Batch INSERT Session + Commands Status='pending' с Priority из CommandPriorityMap. NotifyNewCommandsAsync() → NOTIFY new_command → DB."

### DOCS-CEA-003 — Worker просыпается через conn.WaitAsync() — **ПРОТИВОРЕЧИТ AGENTS/README/ROADMAP!**
- **Файл:** `Docs/CommandExecutionAlgorithm.md:58-66`
- **Категория:** architecture
- **Цитата:** "DB → NOTIFY new_command → Worker: `conn.WaitAsync()` — просыпается мгновенно" — **NB:** это противоречит AGENTS/README/ROADMAP/execution-algorithm.md, которые утверждают что Worker использует **только polling 1 мин**, `new_command LISTEN/NOTIFY` не используется. См. секцию «ПРОТИВОРЕЧИЯ».

### DOCS-CEA-004 — ClaimPendingCommandsAsync(limit=50)
- **Файл:** `Docs/CommandExecutionAlgorithm.md:73-97`
- **Категория:** architecture
- **Цитата:** "ClaimPendingCommandsAsync(limit=50)" — **NB:** execution-algorithm.md указывает `DefaultBatchSize=5` (DOCS-EXEC-011). Противоречие.

### DOCS-CEA-005 — Priority-based партиции (схема)
- **Файл:** `Docs/CommandExecutionAlgorithm.md:104-134`
- **Категория:** architecture
- **Цитата:** "ProcessWithPoolAsync(cmd): Priority 1 → Critical 3 слота, Priority 2 → High 5, Priority 3 → Medium 3, Priority 4 → Low 1, Priority 5+ → Lowest 1; `await pool.WaitAsync(ct)`"

### DOCS-CEA-006 — ValidateFilePath: 3 проверки
- **Файл:** `Docs/CommandExecutionAlgorithm.md:142-154`
- **Категория:** other
- **Цитата:** "1. `Path.GetFullPath()` — защита от path traversal; 2. `File.Exists()` — файл существует?; 3. `AllowedExtensions` — расширение разрешено? Если ошибка — DB: UpdateCommandStatus(Failed), иначе — продолжаем."

### DOCS-CEA-007 — Запуск процесса + асинхронные stdout/stderr
- **Файл:** `Docs/CommandExecutionAlgorithm.md:160-188`
- **Категория:** other
- **Цитата:** "CreateProcessStartInfo: ExecutablePath: \"Revit.exe\"; Arguments: `/command PDF \"file.rvt\"`; WorkingDirectory: из конфига; RedirectStandardOutput/Error = true; `process.Start()` → `UpdateCommandStatus(Processing, ProcessId=PID)` → `BeginOutputReadLine` + `BeginErrorReadLine` (асинхронное чтение в StringBuilder, deadlock 64KB)."

### DOCS-CEA-008 — Завершение процесса (Done / Failed / retry / timeout)
- **Файл:** `Docs/CommandExecutionAlgorithm.md:194-234`
- **Категория:** other
- **Цитата:** "process.WaitForExit(timeoutMs). Timeout → process.Kill(true) + UpdateCommandStatus(Failed, \"Timeout...\"). ExitCode == 0 → UpdateCommandStatus(Done) + NotifyCommandCompletedAsync → NOTIFY command_completed. ExitCode != 0 → ScheduleRetryAsync: Retry #1 +60s (base*2^0), #2 +120s, #3 +240s, #4 +480s, #5 +960s. MaxRetries=5. UPDATE Status='pending', NextRetryAt=...; NOTIFY new_command (будим воркер). Если retry исчерпаны — UpdateCommandStatus(Failed) + NotifyCommandCompletedAsync."

### DOCS-CEA-009 — Уведомление пользователя (payload Parse, NOTIFY)
- **Файл:** `Docs/CommandExecutionAlgorithm.md:240-256`
- **Категория:** other
- **Цитата:** "DB → NOTIFY command_completed → Server: Parse payload `(UserId|CmdId|CmdText|Status|Error)` → Send Telegram message (✅ *PDF* завершена или ❌ *PDF* — ошибка), MarkdownV2 экранир." — **NB:** формат payload здесь `(UserId|CmdId|CmdText|Status|Error)` — 5 полей, тогда как execution-algorithm.md (DOCS-EXEC-019) указывает `UserId|SessionId|Done|Total|ProjectName`. ПРОТИВОРЕЧИЕ.

### DOCS-CEA-010 — Cleanup: _activeProcesses.TryRemove, pool.Release
- **Файл:** `Docs/CommandExecutionAlgorithm.md:262-272`
- **Категория:** other
- **Цитата:** "`_activeProcesses.TryRemove(commandId)`, `pool.Release()` — освобождаем ресурсы и слот партиции."

### DOCS-CEA-011 — Background cleanup (60 sec) + pg_try_advisory_lock(1234567)
- **Файл:** `Docs/CommandExecutionAlgorithm.md:279-305`
- **Категория:** other
- **Цитата:** "ReleaseExpiredLeasesAsync() — UPDATE Commands SET Status='pending', Lease=NULL, ... WHERE Status='processing' AND Lease < @Now; `pg_try_advisory_lock(1234567)` для предотвращения дублирования. ReleaseTimeoutCommandsAsync() — UPDATE ... WHERE Status='processing' AND StartedAt < NOW() - INTERVAL ..."

### DOCS-CEA-012 — Отмена команды (UI/server/DB/Worker)
- **Файл:** `Docs/CommandExecutionAlgorithm.md:309-334`
- **Категория:** other
- **Цитата:** "User → /status → кнопка «⛔ Отменить» → Server → DeleteCommandAsync() → DB: UPDATE Commands SET Status='Deleted' WHERE CommandId=@Id. Если процесс уже выполняется — он завершится штатно. UpdateStatus не перезаписывает Deleted."

### DOCS-CEA-013 — Полный жизненный цикл статусов
- **Файл:** `Docs/CommandExecutionAlgorithm.md:340-368`
- **Категория:** other
- **Цитата:** "pending → processing → (Done | Failed | Deleted). Retry → pending. NOTIFY command_completed → Server → Telegram-уведомление."

### DOCS-CEA-014 — Priority-based partition thresholds
- **Файл:** `Docs/CommandExecutionAlgorithm.md:374-393`
- **Категория:** other
- **Цитата:** "Order by Priority ASC, CreatedAt ASC, CommandId ASC. P<=1 → Critical SemaphoreSlim(3), P<=2 → High(5), P<=3 → Medium(3), P<=4 → Low(1), P<=5 → Lowest(1)."

### DOCS-CEA-015 — Lease TTL = ProcessTimeoutSeconds + 5 мин
- **Файл:** `Docs/CommandExecutionAlgorithm.md:399-413`
- **Категория:** other
- **Цитата:** "Worker захватывает команду: Lease = ProcessTimeoutSeconds + 5 мин (в секундах Unix). При краше Worker-а — другой Worker через 60 секунд: UPDATE Commands SET Status='pending', Lease=NULL, ErrorMessage='Lease expired: ...' WHERE Status='processing' AND Lease < @CurrentTimeSec."

---

## Docs/qodana-setup.md (QODANA-###)

### QODANA-001 — Qodana JetBrains
- **Файл:** `Docs/qodana-setup.md:3`
- **Категория:** other
- **Цитата:** "[Qodana](https://www.jetbrains.com/qodana/) — это платформа для контроля качества кода от JetBrains, которая переносит проверки из Rider/ReSharper в CI/CD."

### QODANA-002 — Запуск через Rider
- **Файл:** `Docs/qodana-setup.md:7-10`
- **Категория:** other
- **Цитата:** "Tools | Qodana | Try Code Analysis with Qodana."

### QODANA-003 — Docker команда
- **Файл:** `Docs/qodana-setup.md:14-17`
- **Категория:** other
- **Цитата:** "`docker run --rm -v ${PWD}:/data/project/ -p 8080:8080 jetbrains/qodana-dotnet --show-report`. Отчет по `http://localhost:8080`."

### QODANA-004 — Qodana в GitHub Actions workflow
- **Файл:** `Docs/qodana-setup.md:21-49`
- **Категория:** other
- **Цитата:** YAML-файл `.github/workflows/qodana.yml`: trigger на workflow_dispatch, pull_request, push (main, master); runs-on ubuntu-latest; JetBrains/qodana-action@v2024.1; env QODANA_TOKEN.

### QODANA-005 — qodana.yaml: linter jetbrains/qodana-dotnet
- **Файл:** `Docs/qodana-setup.md:52-55`
- **Категория:** config-key
- **Цитата:** "Файл `qodana.yaml` в корне проекта содержит основные настройки: linter: используемый образ линтера (`jetbrains/qodana-dotnet`); dotnet: путь к решению (`TelegramBot.slnx`); profile: используемый профиль проверок (`qodana.recommended`)."

### QODANA-006 — CI пайплайн
- **Файл:** `Docs/qodana-setup.md:59-62`
- **Категория:** other
- **Цитата:** "Текущий CI-пайплайн (`.github/workflows/ci.yml`) включает: `dotnet format --verify-no-changes` — проверка стиля кода; `dotnet build` — проверка сборки; `dotnet publish` — публикация артефакта." — **NB:** AGENTS.md:80 утверждает «No CI/CD pipeline or automated tests». Противоречие (см. секцию «ПРОТИВОРЕЧИЯ»).

### QODANA-007 — Qodana не интегрирована в CI
- **Файл:** `Docs/qodana-setup.md:64`
- **Категория:** other
- **Цитата:** "**Примечание:** Qodana пока не интегрирована в CI. Для добавления используйте workflow из раздела «Настройка в CI/CD»."

### QODANA-008 — Тесты отключены, .NET 10
- **Файл:** `Docs/qodana-setup.md:67-68`
- **Категория:** other
- **Цитата:** "Тесты в данном проекте отключены согласно [AGENTS.md](../AGENTS.md). Qodana настроена только на анализ статического кода. Используется .NET 10. Убедитесь, что используемая версия линтера поддерживает этот SDK."

---

## .github/copilot-instructions.md (COPILOT-###)

### COPILOT-001 — Telegram API актуальные методы
- **Файл:** `.github/copilot-instructions.md:4`
- **Категория:** other
- **Цитата:** "Все реализуемые методы Telegram API должны быть актуальными и не устаревшими (без deprecated-подходов)."

### COPILOT-002 — Унификация методов
- **Файл:** `.github/copilot-instructions.md:5`
- **Категория:** other
- **Цитата:** "Поддерживайте хорошую читаемость кода и унифицируйте методы для упрощения редактирования."

---

## ДУБЛИ И ПРОТИВОРЕЧИЯ МЕЖДУ ДОКУМЕНТАМИ

### A. Дубли

#### A1. Диаграмма 4 проектов + BimLib
Дословно идентична в **AGENTS.md:9-16** и **README.md:64-70** (Core ← Data → Server, Worker + BimLib). В ROADMAP.md:11 — текстовое упоминание: "Архитектура с 4 проектами: `Core → Data → Server`, `Worker`".

#### A2. BimLib — структура папок
Одинаковые таблицы папок (Config/Interfaces/Models/Monitor/Native/Services) и одинаковые 6 namespace-ов в **AGENTS.md:88-124**, **README.md:194-219**, **Docs/execution-algorithm.md:154-179**.

#### A3. BimLib DI-регистрация
Идентичный блок из 6 `services.AddSingleton<...>(...)` строк в **AGENTS.md:108-115**, **README.md:204-210**, **Docs/execution-algorithm.md:209-216**.

#### A4. Callback handlers (Priority + префиксы)
**AGENTS.md:185** перечисляет 6 хендлеров с приоритетами; **README.md:270-277** даёт ту же таблицу с дополнительной колонкой префиксов (REQACCESS/APPROVEUSER/REJECTUSER, GOTOPARENT, FILE, PDF/DWG/NWC/IFC/BIMDOC/CLASHREP/AUTORES, SESSIONDETAILS/DELETESESSION/DELETECOMMAND/CONFIRMDELETESESSION/CONFIRMDELETECOMMAND, APPLYCOMMANDS/CANCELCOMMANDSSEL).

#### A5. In-memory счётчик `_sessionRemaining` (ConcurrentDictionary)
Описан в **AGENTS.md:170-175**, **Docs/execution-algorithm.md:729-761**, **ROADMAP.md:109-111**.

#### A6. Удалённые интерфейсы
**AGENTS.md:132**, **ROADMAP.md:197**, **Docs/execution-algorithm.md:178-179**, **README.md:227** все упоминают `IRevitPathResolver`, `IRevitProcessTracker`, `INavisworksProcessTracker`. AGENTS.md и ROADMAP.md дополнительно упоминают `IFileSystemBrowser`, `ITelegramUpdateMapper`.

#### A7. 4 таблицы БД
**AGENTS.md:197**, **README.md:285-290**, **Docs/execution-algorithm.md:890-897** — все называют BotUsers/Sessions/Commands/TrackedMessages.

#### A8. ProjectName в Sessions
**AGENTS.md:199-200**, **ROADMAP.md:103-104** — `ProjectName TEXT` в `Sessions`.

#### A9. Graceful shutdown не нужен
**AGENTS.md (нет прямой строки)**, **ROADMAP.md:96-97**, **ROADMAP.md:146**, **Docs/execution-algorithm.md:379**, **Docs/execution-algorithm.md:1404 (DOC-008)**, **README.md:263** — все зафиксировано.

#### A10. GetCommandStatusAsync удалён
**AGENTS.md:202-203**, **ROADMAP.md:120-121**, **ROADMAP.md:197** — все упоминают удаление.

#### A11. BimLib SupportedOSPlatform("windows")
**AGENTS.md:127**, **README.md:222**, **Docs/execution-algorithm.md:230**.

#### A12. OpenMcdf 3.x API
**AGENTS.md:128**, **README.md:223**, **Docs/execution-algorithm.md:231** — все указывают `RootStorage.OpenRead() → OpenStream() → stream.Read()`.

#### A13. RevitProcessStatus 3 значения
**AGENTS.md:131**, **README.md:226**, **Docs/execution-algorithm.md:234** — `Healthy`, `NotResponding`, `Error`.

#### A14. ProcessHealthHelper.CheckHealth
**AGENTS.md:104/133/141**, **Docs/execution-algorithm.md:166** (упоминание в BimLib-структуре).

#### A15. NpgsqlHelper.CreateOpenConnectionAsync
**AGENTS.md:142**, **ROADMAP.md:205-206** — перенесён из Worker.Services в TelegramBot.Data.

---

### B. Противоречия

#### B1. Кол-во partial-файлов SQL: **4 vs 5**
- **AGENTS.md:19**: "SQL constants in `Sql/` (4 partial files)"
- **AGENTS.md:210**: "SQL constants in `TelegramBot.Data/Sql/` (4 partial files total)"
- **ROADMAP.md:44**: "SQL-запросы, разбитые по сущностям (**5 partial-файлов**)"
- **README.md:96-98** перечисляет 4 файла: `Queries.Schema.cs`, `Queries.Users.cs`, `Queries.Sessions.cs`, `Queries.Commands.cs`.

#### B2. docs/CommandExecutionAlgorithm.md (CEA) — устаревший / противоречивый
Файл `Docs/CommandExecutionAlgorithm.md` — это текстовое представление PlantUML диаграммы `CommandExecutionAlgorithm.puml`. Он содержит несколько утверждений, которые **ПРОТИВОРЕЧАТ** остальной документации (AGENTS.md, README.md, ROADMAP.md, Docs/execution-algorithm.md):

- **CEA-002 / CEA-003**: Утверждает что Server вызывает `NotifyNewCommandsAsync()` → `NOTIFY new_command` (CEA:47-49) и Worker просыпается мгновенно через `conn.WaitAsync()` (CEA:58-66). Это **противоречит**:
  - AGENTS.md (нет упоминания NOTIFY new_command)
  - README.md:35 "Worker забирает pending-команды из БД раз в минуту"
  - ROADMAP.md:148 "`new_command LISTEN/NOTIFY` для Worker ... по текущему решению не требуется"
  - Docs/execution-algorithm.md:285-286 "Никаких LISTEN/NOTIFY — только таймер"
  - Docs/execution-algorithm.md:977 "Worker не ждёт отдельный `new_command` сигнал. Он раз в минуту проверяет PostgreSQL"
  - Docs/execution-algorithm.md:1153 "Polling queue | Worker проверяет очередь каждую минуту без `new_command LISTEN/NOTIFY`"

- **CEA-004**: Утверждает `ClaimPendingCommandsAsync(limit=50)`, тогда как Docs/execution-algorithm.md:309 указывает `DefaultBatchSize=5`. (50 vs 5 — расхождение.)

- **CEA-008 (retry)**: CEA-227 "NOTIFY new_command (будим воркер)" — опять NOTIFY new_command, противоречит остальным документам.

- **CEA-009 (payload)**: CEA-247 "Parse payload `(UserId|CmdId|CmdText|Status|Error)` — 5 полей", тогда как Docs/execution-algorithm.md:695 указывает `UserId|SessionId|Done|Total|ProjectName` (тоже 5 полей, но **совершенно разные**). И CEA отправляет per-command уведомление, тогда как основные документы говорят что уведомление идёт только при завершении всей сессии (per-session).

#### B3. `LeaseTimeoutMin=5` (фиксированное) vs `Lease = ProcessTimeoutSeconds + 5 мин` (долгий TTL)
- **Docs/execution-algorithm.md:454**: "`LeaseTimeoutMin = 5` — Lease истекает через 5 минут" (фиксированное значение, не зависящее от ProcessTimeoutSeconds)
- **ROADMAP.md:53**: "**Lease с долгим TTL** — при захвате команды Lease = ProcessTimeoutSeconds + 5 мин"
- **Docs/CommandExecutionAlgorithm.md:401**: "Worker захватывает команду: Lease = ProcessTimeoutSeconds + 5 мин (в секундах Unix)"
- **Docs/execution-algorithm.md:1147**: "**Lease (долгий TTL)** | Lease устанавливается на `ProcessTimeoutSeconds + 5 мин`, команда не вернётся в очередь раньше таймаута"

**NB:** Возможно `LeaseTimeoutMin=5` в execution-algorithm.md:454 — это имя константы, а не её значение, и она используется для расчёта долгого TTL. Проверить в коде.

#### B4. CI/CD наличие
- **AGENTS.md:80**: "No CI/CD pipeline or automated tests — the only verification is a successful `dotnet build`"
- **Docs/qodana-setup.md:59-62**: Утверждает наличие CI-пайплайна `.github/workflows/ci.yml` с `dotnet format --verify-no-changes`, `dotnet build`, `dotnet publish`.

**NB:** `qodana-setup.md` от 2024/2025, AGENTS.md тоже упоминает в `Known Issues`. Возможно AGENTS.md устарел, либо `.github/workflows/ci.yml` существовал ранее.

#### B5. Наличие полей `GUID`, `RetryCount`, `NextRetryAt`, `Partition` в Commands
- **README.md:289** упоминает `GUID`, `Lease`, `Priority`, `RetryCount`, `NextRetryAt`.
- **Docs/execution-algorithm.md:1042-1062** в SQL упоминает `c.Partition` как колонку Commands (в SELECT ... c.Partition, c.Priority). **Это поле нигде больше не задокументировано** (в AGENTS.md, ROADMAP.md, README.md Partition не упоминается как колонка Commands).

#### B6. `Notifications per-command` vs `per-session`
- **Docs/CommandExecutionAlgorithm.md:242-256**: Сервер слушает NOTIFY command_completed и отправляет per-command сообщение (✅ *PDF* завершена / ❌ *PDF* — ошибка).
- **AGENTS.md:165-167** / **Docs/execution-algorithm.md:658-661**: Уведомление отправляется **только после завершения всей сессии** (per-session сводка, не per-command).
- **ROADMAP.md:74-77**: "Уведомления приходят только при завершении всей сессии (сводка: N ✅, M ❌)".

#### B7. Worker Services папка — наличие BimLibLogFilter
- **ROADMAP.md:198** ("Удалены лишние интерфейсы") не упоминает BimLibLogFilter.
- **README.md:264** упоминает BimLibLogFilter как `TelegramBot.Worker/Services`.
- **AGENTS.md** не упоминает BimLibLogFilter вообще.

#### B8. Документы CLAUDE.md и README.TOKEN.md
- **README.md:13,14**, **ROADMAP.md:256,257,259** ссылаются на `CLAUDE.md` и `README.TOKEN.md`.
- В репозитории эти файлы **отсутствуют** (проверено: `ls C:\Users\y.zhumabayev\Repository\TelegramBot | grep -iE "(CLAUDE|TOKEN)"` → пусто).

#### B9. Пространства имён
- **AGENTS.md:241-243** и **README.md** (косвенно через дерево) указывают namespace `TelegramBot.Server.Services.Infrastructure.Telegram`. **README.md:111** упоминает `Services/Infrastructure/Telegram`. **AGENTS.md:243** упоминает `TelegramBot.Server.Services.Infrastructure.Telegram`.

#### B10. Структура `Sql/`
- **README.md:95-98** перечисляет 4 файла: `Queries.Schema.cs`, `Queries.Users.cs`, `Queries.Sessions.cs`, `Queries.Commands.cs`. Это совпадает с **AGENTS.md (4 partial files)**, но **противоречит ROADMAP.md:44 (5 partial files)**. Фактически надо сверить с `ls TelegramBot.Data/Sql/`.

---

### C. Возможные дубли в `Docs/`

- `Docs/execution-algorithm.md` (1426 строк) — **основная** детальная спецификация алгоритма.
- `Docs/CommandExecutionAlgorithm.md` (414 строк) — текстовое представление PUML-диаграммы `CommandExecutionAlgorithm.puml`.

**Вердикт:** Содержимое **пересекается по теме**, но `CommandExecutionAlgorithm.md` содержит **устаревшие/противоречивые утверждения** (NOTIFY new_command, per-command уведомления, payload формат, limit=50). Скорее всего файл не обновлялся после v1.x и описывает более раннее состояние системы.

Файл `CommandExecutionAlgorithm.puml`, упомянутый в CEA.md:3 ("Текстовое представление диаграммы `CommandExecutionAlgorithm.puml`"), в репозитории **не обнаружен** (нужно проверить отдельно — вероятно отсутствует).

---

### D. Известные «оговорки про stale-документацию» в самих документах

- **AGENTS.md:342**: "`/// <inheritdoc/>` comments on methods that no longer implement interfaces (e.g., `RevitPathResolver`, `RevitProcessTracker`) are stale but harmless — replace with proper `<summary>` when editing nearby"
- **AGENTS.md:340**: "Keep secrets out of committed config files — use `TelegramBot.Server/appsettings.Local.json` (gitignored) or env var `TelegramBot__Token`; never hardcode tokens"
- **ROADMAP.md:188-189** (v1.3): "**Синхронизировать документацию с реальным алгоритмом** — обновить `Docs/execution-algorithm.md`, `README.md`, `AGENTS.md` и `CLAUDE.md` после упрощения кода" (planned, not done)
- **Docs/execution-algorithm.md:1391-1408** (DOC-001..011) — таблица "Известные ограничения и технический долг" со статусами ✅ Реализовано / В планах / Улучшение

---

## Сводная статистика

| Файл | ID префикс | Кол-во утверждений |
|------|-----------|-------------------|
| AGENTS.md | AGENTS- | 83 |
| README.md | README- | 44 |
| ROADMAP.md | ROADMAP- | 37 |
| Docs/execution-algorithm.md | DOCS-EXEC- | 38 |
| Docs/CommandExecutionAlgorithm.md | DOCS-CEA- | 15 |
| Docs/qodana-setup.md | QODANA- | 8 |
| .github/copilot-instructions.md | COPILOT- | 2 |
| **ИТОГО** | | **227** |

Категории:
- architecture: ~50
- class-name: ~30
- namespace: ~7
- config-key: ~30
- sql-table / sql-field: ~25
- callback-prefix / command-code: ~10
- dependency: ~10
- build-cmd / run-cmd: ~10
- other: ~55
