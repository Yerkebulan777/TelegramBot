# Документационные утверждения (Doc Claims)

> Сборник проверяемых утверждений из всех файлов документации репозитория `TelegramBot`.
> Файл предназначен для последующей сверки с фактическим кодом. Цитаты — verbatim,
> с указанием источника `файл:строка` (или диапазона строк).
>
> Категории: `architecture` | `class-name` | `namespace` | `config-key` |
> `sql-table` | `callback-prefix` | `command-code` | `dependency` |
> `build-cmd` | `run-cmd` | `other`
>
> ID: `AGENTS-NNN`, `README-NNN`, `ROADMAP-NNN`, `DOCS-EXEC-NNN`, `DOCS-CEA-NNN`,
> `QODANA-NNN`, `COPILOT-NNN`.

---

## AGENTS.md (412 строк, 1 файл)

### Структура проекта (architecture / class-name / namespace)

- **AGENTS-001** — [AGENTS.md:7] `Telegram bot using long-polling, split into **4 projects** (`.slnx`). No webhooks, no MVC controllers. All services are **Singletons**.` — category: architecture
- **AGENTS-002** — [AGENTS.md:18] `**TelegramBot.Core** — Models, DTOs, interfaces, config, constants. Zero Telegram SDK dependency.` — category: architecture
- **AGENTS-003** — [AGENTS.md:19] `**TelegramBot.Data** — PostgreSQL persistence via Dapper + Npgsql. References Core only. SQL constants in `Sql/` (5 partial files).` — category: architecture
- **AGENTS-004** — [AGENTS.md:20] `**TelegramBot.Server** — Telegram infrastructure, application services, handlers, hosting, helpers. References Core + Data.` — category: architecture
- **AGENTS-005** — [AGENTS.md:21] `**TelegramBot.Worker** — Background service for executing Revit/Navisworks/AI tasks. Polls PostgreSQL for pending commands. References Core + Data. BimLib is embedded inside this project as `Worker/BimLib/` (not a separate project).` — category: architecture
- **AGENTS-006** — [AGENTS.md:23] `**Note:** BimLib is **not a separate project** — it lives as a directory inside Worker (`TelegramBot.Worker/BimLib/`). Namespaces remain `TelegramBot.BimLib.*`. OpenMcdf dependency is in Worker's `.csproj`.` — category: architecture
- **AGENTS-007** — [AGENTS.md:134] `BimLib is **not a separate project** — it lives as a directory inside Worker. No `TelegramBot.BimLib.csproj` exists.` — category: architecture

### Build & Run команды (build-cmd / run-cmd)

- **AGENTS-008** — [AGENTS.md:31] `dotnet build TelegramBot.slnx` — category: build-cmd
- **AGENTS-009** — [AGENTS.md:34] `dotnet run --project TelegramBot.Server/TelegramBot.Server.csproj` — category: run-cmd
- **AGENTS-010** — [AGENTS.md:37] `dotnet run --project TelegramBot.Worker/TelegramBot.Worker.csproj` — category: run-cmd
- **AGENTS-011** — [AGENTS.md:40] `dotnet publish TelegramBot.Server/TelegramBot.Server.csproj -c Release` — category: build-cmd
- **AGENTS-012** — [AGENTS.md:43] `dotnet format TelegramBot.slnx` — category: build-cmd
- **AGENTS-013** — [AGENTS.md:46] `**Tests are intentionally disabled for this project.** Do not add test projects, do not add unit/integration tests, and do not run `dotnet test`.` — category: other
- **AGENTS-014** — [AGENTS.md:46] `After making changes, verify correctness by building successfully with `dotnet build TelegramBot.slnx`.` — category: build-cmd

### Конфигурация (config-key / architecture)

- **AGENTS-015** — [AGENTS.md:54] `TelegramBot.Server/appsettings.json` — committed, contains Serilog config, `FileSystem` options, and `ConnectionStrings:Postgres`` — category: config-key
- **AGENTS-016** — [AGENTS.md:55] `TelegramBot.Server/appsettings.Local.json` — **gitignored**, put secrets here (bot token, local overrides)` — category: config-key
- **AGENTS-017** — [AGENTS.md:56] `TelegramBot.Worker/appsettings.json` — committed, contains `ConnectionStrings:Postgres`` — category: config-key
- **AGENTS-018** — [AGENTS.md:58] `TelegramBot:Token` — bot token (also settable via env var `TelegramBot__Token`)` — category: config-key
- **AGENTS-019** — [AGENTS.md:59] `TelegramBot:AdminUserIds` — long[] of admin Telegram IDs (also settable via `TelegramBot__AdminUserIds__0`, `__1`, etc.)` — category: config-key
- **AGENTS-020** — [AGENTS.md:60] `FileSystem:RootPath` — filesystem browser root (validated on startup via `FileSystemOptions`)` — category: config-key
- **AGENTS-021** — [AGENTS.md:61] `ConnectionStrings:Postgres` — PostgreSQL connection string (defaults to `"Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres"`)` — category: config-key
- **AGENTS-022** — [AGENTS.md:62] `RateLimit:MaxFilesPerUserPerDay` — daily per-user file quota; `0` disables it` — category: config-key
- **AGENTS-023** — [AGENTS.md:63] `Worker:CompletedSessionRetentionDays` — auto-cleanup retention for inactive sessions; `0` disables it` — category: config-key

### Архитектура request flow (architecture / class-name)

- **AGENTS-024** — [AGENTS.md:70] `Telegram API -> TelegramBotHostedService (polling)` — category: class-name
- **AGENTS-025** — [AGENTS.md:71] `-> TelegramUpdateMapper (Update -> MessageDto | CallbackQueryDto)` — category: class-name
- **AGENTS-026** — [AGENTS.md:72] `-> CommandAppService.HandleUserCommandAsync (text commands)` — category: class-name
- **AGENTS-027** — [AGENTS.md:73] `├── /start bypasses access check → registration or help` — category: architecture
- **AGENTS-028** — [AGENTS.md:74] `└── other commands → BotUsers.Status must be Approved` — category: architecture
- **AGENTS-029** — [AGENTS.md:75] `-> ICallbackDispatcher -> CallbackDispatcher.DispatchAsync (inline keyboard callbacks)` — category: class-name
- **AGENTS-030** — [AGENTS.md:76] `├── REQACCESS/APPROVEUSER/REJECTUSER bypass access check` — category: callback-prefix
- **AGENTS-031** — [AGENTS.md:77] `└── all other callbacks → user must be Approved` — category: architecture

### BimLib (architecture / class-name / namespace)

- **AGENTS-032** — [AGENTS.md:82] `BimLib is a **Windows-only** set of modules located inside the Worker project (`TelegramBot.Worker/BimLib/`).` — category: architecture
- **AGENTS-033** — [AGENTS.md:88] `| `Config/` | `BimIntegrationOptions` — min/max supported Revit version, install root path |` — category: class-name
- **AGENTS-034** — [AGENTS.md:89] `| `Interfaces/` | `IRevitVersionDetector`, `INavisworksPathResolver` |` — category: class-name
- **AGENTS-035** — [AGENTS.md:90] `| `Models/` | `RevitDetectedVersion`, `RevitProcessHealth` (status: Healthy/NotResponding/Error) |` — category: class-name
- **AGENTS-036** — [AGENTS.md:91] `| `Monitor/` | `RevitProcessTracker`, `NavisworksProcessTracker`, `ProcessHealthHelper`, `DialogDismisser`, `WindowUtil`, `WindowInfo` |` — category: class-name
- **AGENTS-037** — [AGENTS.md:92] `| `Native/` | P/Invoke WinAPI declarations: `User32`, `Win32Consts` |` — category: class-name
- **AGENTS-038** — [AGENTS.md:93] `| `Services/` | `RevitVersionDetector`, `RevitPathResolver`, `NavisworksPathResolver` |` — category: class-name
- **AGENTS-039** — [AGENTS.md:99] `| `RevitVersionDetector` | Reads OLE stream `BasicFileInfo` from .rvt/.rfa via OpenMcdf to extract `Format: YYYY` |` — category: class-name
- **AGENTS-040** — [AGENTS.md:100] `| `RevitPathResolver` | Finds `Revit.exe` path via Windows Registry (`HKLM\SOFTWARE\Autodesk\Revit\{version}`) |` — category: class-name
- **AGENTS-041** — [AGENTS.md:101] `| `NavisworksPathResolver` | Finds `Navisworks.exe`/`FileConvert.exe` via Windows Registry |` — category: class-name
- **AGENTS-042** — [AGENTS.md:102] `| `RevitProcessTracker` | Monitors Revit processes: responsiveness, dialog dismissal, PID tracking (uses `ProcessHealthHelper`) |` — category: class-name
- **AGENTS-043** — [AGENTS.md:103] `| `NavisworksProcessTracker` | Monitors Navisworks processes (Roamer, FileConvert) (uses `ProcessHealthHelper`) |` — category: class-name
- **AGENTS-044** — [AGENTS.md:104] `| `ProcessHealthHelper` | Static helper for `CheckHealth()` — shared between both process trackers |` — category: class-name
- **AGENTS-045** — [AGENTS.md:105] `| `DialogDismisser` | Auto-closes modal Revit dialogs (#32770) by finding and clicking known buttons |` — category: class-name
- **AGENTS-046** — [AGENTS.md:109-114] `services.AddSingleton<IRevitVersionDetector, RevitVersionDetector>(); services.AddSingleton<RevitPathResolver>(); services.AddSingleton<RevitProcessTracker>(); services.AddSingleton<DialogDismisser>(); services.AddSingleton<INavisworksPathResolver, NavisworksPathResolver>(); services.AddSingleton<NavisworksProcessTracker>();` — category: architecture
- **AGENTS-047** — [AGENTS.md:116] `Requires `BimIntegrationOptions` config section in Worker's `appsettings.json`.` — category: config-key
- **AGENTS-048** — [AGENTS.md:119-124] `Namespaces: TelegramBot.BimLib.Config, TelegramBot.BimLib.Interfaces, TelegramBot.BimLib.Models, TelegramBot.BimLib.Monitor, TelegramBot.BimLib.Native, TelegramBot.BimLib.Services` — category: namespace
- **AGENTS-049** — [AGENTS.md:127] `BimLib is `[SupportedOSPlatform("windows")]` — never run or test on non-Windows.` — category: dependency
- **AGENTS-050** — [AGENTS.md:128] `OpenMcdf 3.x is used to parse OLE Structured Storage (.rvt files). API: `RootStorage.OpenRead()` → `root.OpenStream()` → `stream.Read()`.` — category: dependency
- **AGENTS-051** — [AGENTS.md:129] `Registry access uses `Microsoft.Win32.Registry` — only works on Windows.` — category: dependency
- **AGENTS-052** — [AGENTS.md:131] `RevitProcessStatus` enum has only 3 values: `Healthy`, `NotResponding`, `Error`.` — category: class-name
- **AGENTS-053** — [AGENTS.md:132] `Removed interfaces (concrete classes only): `IRevitPathResolver`, `IRevitProcessTracker`, `INavisworksProcessTracker` — they had no consumers outside BimLib.` — category: class-name

### Shared Static Helpers (class-name)

- **AGENTS-054** — [AGENTS.md:140] `| `HandlerHelpers` | `Server/Services/Application/Handlers/HandlerHelpers.cs` | `SendActionsReplyKeyboardAsync()` — универсальный метод |` — category: class-name
- **AGENTS-055** — [AGENTS.md:142] `| `NpgsqlHelper` | `TelegramBot.Data/NpgsqlHelper.cs` | `CreateOpenConnectionAsync()` |` — category: class-name
- **AGENTS-056** — [AGENTS.md:143] `| `BimLibLogFilter` | `Worker/Services/BimLibLogFilter.cs` |` — category: class-name

### Constants (class-name / callback-prefix / command-code)

- **AGENTS-057** — [AGENTS.md:149] `All constants are located in `TelegramBot.Core/Constants/`.` — category: namespace
- **AGENTS-058** — [AGENTS.md:153] `| `CallbackPrefixes.cs` | Inline keyboard callback prefixes | `GoToParent`, `File`, `Pdf`, `SessionDetails`, `DeleteSession`, `RequestAccess`, etc. |` — category: callback-prefix
- **AGENTS-059** — [AGENTS.md:154] `| `CommandCodes.cs` | Export command identifiers | `Pdf`, `Dwg`, `Nwc`, `Ifc`, `BimDoc`, `ClashRep`, `AutoRes` |` — category: command-code
- **AGENTS-060** — [AGENTS.md:155] `| `Statuses.cs` | Entity statuses | `Pending`, `Processing`, `Done`, `Failed`, `Deleted`, `FinalStatuses`, `ActiveStatuses` |` — category: class-name
- **AGENTS-061** — [AGENTS.md:156] `| `CommandPriorities.cs` | Worker queue priority levels | `Critical`, `High`, `Medium`, `Low`, `Default` |` — category: class-name
- **AGENTS-062** — [AGENTS.md:157] `| `ButtonTexts.cs` | Reply keyboard button labels | `Apply`, `Confirm`, `Cancel` |` — category: class-name
- **AGENTS-063** — [AGENTS.md:160] `All callback prefixes end with `:` (colon) for data concatenation` — category: callback-prefix
- **AGENTS-064** — [AGENTS.md:161] `Statuses.FinalStatuses` includes `Done`, `Failed`, `Deleted`` — category: class-name
- **AGENTS-065** — [AGENTS.md:162] `Statuses.ActiveStatuses` includes `Pending`, `Processing`` — category: class-name
- **AGENTS-066** — [AGENTS.md:163] `Command codes match callback prefix names (e.g., `CommandCodes.Pdf` = `"PDF"`, `CallbackPrefixes.Pdf` = `"PDF:"`)` — category: callback-prefix
- **AGENTS-067** — [AGENTS.md:166] `Deprecated: `CommandStatuses.cs` — use `Statuses` instead. The old class is marked `[Obsolete]` but still works for backward compatibility.` — category: class-name

### Task execution flow (architecture / class-name / sql-table)

- **AGENTS-068** — [AGENTS.md:173] `SlashCommandService.ConfirmFileSelectionAsync()` — category: class-name
- **AGENTS-069** — [AGENTS.md:175] `├── dataService.CreateSessionWithCommandsAsync() -- INSERT INTO Commands (ProjectName)` — category: class-name
- **AGENTS-070** — [AGENTS.md:179] `LISTEN/NOTIFY new_tasks (мгновенная реакция) + fallback polling (5 мин)` — category: architecture
- **AGENTS-071** — [AGENTS.md:180] `dataService.ClaimPendingCommandsAsync() -- FOR UPDATE SKIP LOCKED` — category: class-name
- **AGENTS-072** — [AGENTS.md:182] `dataService.UpdateCommandStatusAsync() -- UPDATE Status='Done'/'Failed'` — category: class-name
- **AGENTS-073** — [AGENTS.md:183] `CompleteClaimedCommandAsync() -- декремент batch-счётчика + проверка финальности по БД` — category: class-name
- **AGENTS-074** — [AGENTS.md:184] `└── dataService.NotifyCommandCompletedAsync() -- NOTIFY command_completed` — category: class-name
- **AGENTS-075** — [AGENTS.md:188] `CommandNotificationService (Server)` — category: class-name
- **AGENTS-076** — [AGENTS.md:189] `Получает NOTIFY → парсит payload (UserId|SessionId|Done|Total|ProjectName)` — category: architecture
- **AGENTS-077** — [AGENTS.md:195] `Worker использует `ConcurrentDictionary<int, int> _sessionRemaining`` — category: class-name
- **AGENTS-078** — [AGENTS.md:201] `DI is wired in `TelegramBot.Server/Extensions/DependencyInjectionExtensions.cs`.` — category: namespace
- **AGENTS-079** — [AGENTS.md:201] `The filesystem root comes from `FileSystemOptions` (bound to `"FileSystem"` config section).` — category: config-key
- **AGENTS-080** — [AGENTS.md:201] `The Worker uses `PostgresDataService` registered directly in `Program.cs`.` — category: class-name
- **AGENTS-081** — [AGENTS.md:203] `IFileSystemBrowser` and `ITelegramUpdateMapper` interfaces were removed — their consumers now depend on concrete types `FileSystemBrowser` and `TelegramUpdateMapper` directly` — category: class-name

### Callback Handling (callback-prefix / class-name)

- **AGENTS-082** — [AGENTS.md:207] `CallbackDispatcher` (implements `ICallbackDispatcher`) routes callbacks to the first `ICallbackHandler` that `CanHandle()` the prefix (sorted by `Priority`, lower = first). All handlers extend `CallbackHandlerBase`.` — category: class-name
- **AGENTS-083** — [AGENTS.md:209] `Handler hierarchy: `AccessRequestHandler` (Priority 0) > `FileNavigationHandler` (10) > `FileSelectionHandler` (20) > `CommandToggleHandler`, `SessionManagementHandler`, `CommandSelectionHandler` (100).` — category: class-name
- **AGENTS-084** — [AGENTS.md:211] `CallbackHandlerBase.HandleAsync()` does NOT catch exceptions — they propagate to `CallbackDispatcher.DispatchAsync()`, which catches `Exception`, logs it, and continues to the next handler.` — category: architecture
- **AGENTS-085** — [AGENTS.md:213] `Use `CallbackDataParser.Parse(data)` (from `ParsedCallback.cs`) to get a `ParsedCallback`, then match with `parsed.Is(CallbackPrefixes.GoToParent)`.` — category: class-name
- **AGENTS-086** — [AGENTS.md:215] `SessionManagementHandler` manages `/status` actions via `SESSIONDETAILS:`, `DELETESESSION:`, `DELETECOMMAND:`, `CONFIRMDELETESESSION:`, and `CONFIRMDELETECOMMAND:`. Delete buttons first show a confirmation dialog` — category: callback-prefix
- **AGENTS-087** — [AGENTS.md:215] `the «⛔ Отменить» button for a running command uses the same soft-delete path as command deletion: `Status = 'Deleted'`.` — category: architecture
- **AGENTS-088** — [AGENTS.md:217] `For Markdown escaping, use `MarkdownHelper` from `TelegramBot.Server/Helpers/`.` — category: class-name

### Database (sql-table / class-name)

- **AGENTS-089** — [AGENTS.md:221] `Tables: `BotUsers`, `Sessions`, `Commands`, `TrackedMessages`.` — category: sql-table
- **AGENTS-090** — [AGENTS.md:221] `Message tracking is fully DB-backed — no in-memory state.` — category: architecture
- **AGENTS-091** — [AGENTS.md:221] `Soft-delete only — set `Status = 'Deleted'`, never `DELETE FROM`.` — category: architecture
- **AGENTS-092** — [AGENTS.md:221] `Worker auto-cleanup also uses soft-delete for inactive sessions older than `Worker:CompletedSessionRetentionDays`.` — category: architecture
- **AGENTS-093** — [AGENTS.md:223] `Commands` table includes `Partition` field — partition threshold для priority-based пулов процессов.` — category: sql-table
- **AGENTS-094** — [AGENTS.md:225-226] `Sessions` table now includes `ProjectName TEXT` — имя проекта записывается при создании сессии, отображается в `/status` и в уведомлениях о завершении.` — category: sql-table
- **AGENTS-095** — [AGENTS.md:228-229] `GetCommandStatusAsync` removed — was dead code. Deleted commands never appear as `'pending'` in `ClaimPendingCommandsAsync`` — category: class-name
- **AGENTS-096** — [AGENTS.md:231-233] `CountPendingProcessingBySessionAsync` added — используется в `CompleteClaimedCommandAsync` для проверки, не осталось ли ещё pending/processing команд в БД` — category: class-name
- **AGENTS-097** — [AGENTS.md:235] `Database: **PostgreSQL** via Npgsql. Initialized at startup via `host.InitializeDatabaseAsync()` + `host.SeedAdminUsersAsync()`.` — category: architecture
- **AGENTS-098** — [AGENTS.md:236] `All data access uses **Dapper** (`TelegramBot.Data/PostgresDataService.cs`).` — category: class-name
- **AGENTS-099** — [AGENTS.md:236] `Connection creation is unified via `CreateConnectionAsync()` helper (replaces ~15 manual `new NpgsqlConnection + OpenAsync` patterns).` — category: class-name
- **AGENTS-100** — [AGENTS.md:236] `SQL constants in `TelegramBot.Data/Sql/` (5 partial files total: `Queries.Schema.cs`, `Queries.Users.cs`, `Queries.Sessions.cs`, `Queries.Commands.cs`, `Queries.TrackedMessages.cs`).` — category: namespace

### C# Language Features (other)

- **AGENTS-101** — [AGENTS.md:244] `Target framework: .NET 10 (`net10.0`)` — category: dependency
- **AGENTS-102** — [AGENTS.md:245] `Nullable reference types: enabled — always annotate nullability (`string?`, `T?`)` — category: other
- **AGENTS-103** — [AGENTS.md:246] `Implicit usings: enabled — do not add `using System;` etc. unless needed beyond the implicit set` — category: other
- **AGENTS-104** — [AGENTS.md:247] `File-scoped namespaces` required: `namespace TelegramBot.Core.Models;`` — category: other
- **AGENTS-105** — [AGENTS.md:248] `Primary constructors` (C# 12) are used in newer services` — category: other

### Naming Conventions (other)

- **AGENTS-106** — [AGENTS.md:254] `Classes: PascalCase — CommandAppService` — category: other
- **AGENTS-107** — [AGENTS.md:255] `Interfaces: I + PascalCase — IDataService` — category: other
- **AGENTS-108** — [AGENTS.md:256] `Methods: PascalCase — HandleCallbackAsync` — category: other
- **AGENTS-109** — [AGENTS.md:257] `Async methods: suffix Async — InitializeDatabaseAsync` — category: other
- **AGENTS-110** — [AGENTS.md:258] `Private fields: _camelCase — _logger, _sessionManager` — category: other
- **AGENTS-111** — [AGENTS.md:259] `Properties: PascalCase — SelectedFiles, CurrentPath` — category: other
- **AGENTS-112** — [AGENTS.md:260] `Local variables: camelCase — chatId, sessionId` — category: other
- **AGENTS-113** — [AGENTS.md:261] `Parameters: camelCase — userId, cancellationToken` — category: other

### Namespace Conventions (namespace)

- **AGENTS-114** — [AGENTS.md:266] `TelegramBot.Core.Models, TelegramBot.Core.DTOs, TelegramBot.Core.Interfaces, TelegramBot.Core.Config, TelegramBot.Core.Constants` — category: namespace
- **AGENTS-115** — [AGENTS.md:267] `TelegramBot.Data` — category: namespace
- **AGENTS-116** — [AGENTS.md:268] `TelegramBot.Server.Services.Application, TelegramBot.Server.Services.Infrastructure.Telegram, TelegramBot.Server.Helpers` — category: namespace
- **AGENTS-117** — [AGENTS.md:269] `TelegramBot.Worker.Services` — category: namespace

### Imports / Using Directives (other)

- **AGENTS-118** — [AGENTS.md:273] `Place `using` directives at the top of the file, before the namespace` — category: other
- **AGENTS-119** — [AGENTS.md:274] `Order: framework namespaces, then third-party (`Dapper`, `Npgsql`, `Serilog`, `Telegram.Bot`), then project-internal (`TelegramBot.*`)` — category: other

### Dependency Injection (architecture / other)

- **AGENTS-120** — [AGENTS.md:279] `Register all new services as **Singletons** in `DependencyInjectionExtensions.cs`` — category: architecture
- **AGENTS-121** — [AGENTS.md:280] `Use `_ = services.AddSingleton<IFoo, Foo>()` (discard the fluent return value)` — category: other
- **AGENTS-122** — [AGENTS.md:281-294] `Inject dependencies via **primary constructors** (C# 12). Parameters are captured automatically — do NOT add redundant `private readonly` fields for direct copies` — category: other
- **AGENTS-123** — [AGENTS.md:295-297] `Fields that **transform** parameters are fine: `private readonly FileSystemOptions _options = options.Value;` ... Fields that create **new instances** are fine: `private readonly ConcurrentDictionary<int, Process> _activeProcesses = new();`` — category: other
- **AGENTS-124** — [AGENTS.md:298] `New callback handlers: implement `ICallbackHandler`, extend `CallbackHandlerBase`, register in `AddCallbackHandlers()`` — category: other

### Async / Await (other)

- **AGENTS-125** — [AGENTS.md:302] `All async methods return `Task` or `Task<T>` — never `async void`` — category: other
- **AGENTS-126** — [AGENTS.md:303] `Always suffix async methods with `Async`` — category: other
- **AGENTS-127** — [AGENTS.md:304] `Do **not** use `ConfigureAwait(false)` — this is an application, not a library` — category: other
- **AGENTS-128** — [AGENTS.md:305] `CancellationToken` is threaded from `BackgroundService.ExecuteAsync`; inner methods generally do not require it unless doing I/O loops` — category: other

### Error Handling (other)

- **AGENTS-129** — [AGENTS.md:309] `Startup: wrapped in `try/catch` with `Log.Fatal` in `Program.cs` — do not remove` — category: other
- **AGENTS-130** — [AGENTS.md:310] `Telegram API calls: catch `ApiRequestException` specifically, log as `LogWarning`, let the bot continue` — category: other
- **AGENTS-131** — [AGENTS.md:311] `TelegramOutputService` has retry logic for HTTP 429 (rate limiting) via `ExecuteWithRetryAsync`` — category: class-name
- **AGENTS-132** — [AGENTS.md:314] `Callback exceptions: `CallbackDispatcher` catches + logs; **`CallbackHandlerBase` does not** (no double logging)` — category: other
- **AGENTS-133** — [AGENTS.md:315] `Worker: outer retry loop reconnects on PostgreSQL connection loss (5 sec delay)` — category: other

### Logging (other)

- **AGENTS-134** — [AGENTS.md:319] `Use `ILogger<T>` injected via constructor (Serilog backs it)` — category: dependency
- **AGENTS-135** — [AGENTS.md:320-323] `Use structured logging with message templates — **not** string interpolation` — category: other
- **AGENTS-136** — [AGENTS.md:324] `Log levels: `LogDebug` for diagnostics, `LogInformation` for normal flow, `LogWarning` for recoverable issues, `LogError` / `Log.Fatal` for failures` — category: other

### Collections & Thread Safety (other)

- **AGENTS-137** — [AGENTS.md:328] `UserSession` uses fine-grained locks (`_commandLock`, `_selectionLock`) — follow this pattern for new mutable state` — category: class-name
- **AGENTS-138** — [AGENTS.md:329] `Paths in callback data are passed directly (no `PathMap`/tokens) since v1.1 refactoring` — category: other
- **AGENTS-139** — [AGENTS.md:330] `For new shared dictionaries, prefer `ConcurrentDictionary<,>`` — category: other

### SQL / Data Access (other / sql-table)

- **AGENTS-140** — [AGENTS.md:334] `Use `await using var conn = await CreateConnectionAsync()` — connection creation is unified via a private helper in `PostgresDataService`` — category: other
- **AGENTS-141** — [AGENTS.md:335] `Use **Dapper** for all queries (no raw `NpgsqlCommand`/`NpgsqlDataReader`)` — category: dependency
- **AGENTS-142** — [AGENTS.md:336] `SQL statements go in verbatim string literals (`@"..."`)` — category: other
- **AGENTS-143** — [AGENTS.md:337] `Use parameterized queries — never string-concatenate user input into SQL` — category: other
- **AGENTS-144** — [AGENTS.md:338] `Soft-delete only: `SET Status = 'Deleted'`, never `DELETE FROM`` — category: other
- **AGENTS-145** — [AGENTS.md:339] `For transactions, use `conn.BeginTransactionAsync()`` — category: other
- **AGENTS-146** — [AGENTS.md:340] `Use `RETURNING` clause for INSERT to get generated IDs (not `last_insert_rowid()`)` — category: other
- **AGENTS-147** — [AGENTS.md:341] `Use `ON CONFLICT DO NOTHING / DO UPDATE` for upserts (not `INSERT OR IGNORE/REPLACE`)` — category: other
- **AGENTS-148** — [AGENTS.md:342] `PostgreSQL data types: `TIMESTAMPTZ` for dates, `SERIAL` for auto-increment, `BIGINT` for user IDs` — category: sql-table

### Telegram Messages (other)

- **AGENTS-149** — [AGENTS.md:346] `Plain messages: `ParseMode.MarkdownV2` — escape special characters with `MarkdownHelper.EscapeMarkdownV2()`` — category: other
- **AGENTS-150** — [AGENTS.md:347] `Messages with inline keyboards: `ParseMode.Markdown` — escape with `MarkdownHelper.EscapeMarkdown()`` — category: other
- **AGENTS-151** — [AGENTS.md:348] `Do not mix the two parse modes` — category: other
- **AGENTS-152** — [AGENTS.md:349] `All Telegram API methods must be current — do not use deprecated approaches` — category: other

### General (other)

- **AGENTS-153** — [AGENTS.md:353] `XML doc comments (`/// <summary>`) on new interface methods` — category: other
- **AGENTS-154** — [AGENTS.md:354] `Use `required` keyword on model properties that must always be set` — category: other
- **AGENTS-155** — [AGENTS.md:355] `Prefer `??` and `?? throw new InvalidOperationException(...)` over unchecked null dereferences` — category: other
- **AGENTS-156** — [AGENTS.md:357] `Extract shared static helpers (`HandlerHelpers`, `NpgsqlHelper`) when the same 5+ line pattern appears in multiple files` — category: other
- **AGENTS-157** — [AGENTS.md:358] `Use `dotnet format --diagnostics IDE0005` to remove unused `using` directives` — category: build-cmd

### Known Issues (other)

- **AGENTS-158** — [AGENTS.md:364] `.editorconfig` exists with naming rules, formatting preferences, and `generated_code = true` markers for data service and handlers — `dotnet format` respects these` — category: other
- **AGENTS-159** — [AGENTS.md:365] `CI pipeline exists (`.github/workflows/ci.yml`) — runs `dotnet build` and `dotnet publish` on push/PR. No automated tests — the only verification is a successful `dotnet build`` — category: other
- **AGENTS-160** — [AGENTS.md:366] `Keep secrets out of committed config files — use `TelegramBot.Server/appsettings.Local.json` (gitignored) or env var `TelegramBot__Token`; never hardcode tokens` — category: other
- **AGENTS-161** — [AGENTS.md:367] `PostgreSQL connection string in committed `appsettings.json` uses default `postgres/postgres` credentials — override via `appsettings.Local.json` or env var `ConnectionStrings__Postgres`` — category: other
- **AGENTS-162** — [AGENTS.md:368] `/// <inheritdoc/>` comments on methods that no longer implement interfaces (e.g., `RevitPathResolver`, `RevitProcessTracker`) are stale but harmless — replace with proper `<summary>` when editing nearby` — category: other

### GitNexus section (other)

- **AGENTS-163** — [AGENTS.md:373] `This project is indexed by GitNexus as **TelegramBot** (1463 symbols, 3693 relationships, 123 execution flows).` — category: other
- **AGENTS-164** — [AGENTS.md:379] `MUST run impact analysis before editing any symbol.` — category: other
- **AGENTS-165** — [AGENTS.md:380] `MUST run `gitnexus_detect_changes()` before committing` — category: other
- **AGENTS-166** — [AGENTS.md:387] `NEVER edit a function, class, or method without first running `gitnexus_impact` on it.` — category: other

---

## README.md (179 строк)

### Заголовок / обзор (architecture)

- **README-001** — [README.md:3] `Telegram-бот для навигации по файловой системе и управления сессиями экспорта/автоматизации. Задачи выполняются асинхронно через Worker-процесс с PostgreSQL-очередью (LISTEN/NOTIFY + fallback polling).` — category: architecture

### Ссылки на документы (other)

- **README-002** — [README.md:9-11] `Документация: ROADMAP.md, AGENTS.md, Docs/execution-algorithm.md` — category: other
- **README-003** — [README.md:11] `Docs/execution-algorithm.md` — Алгоритм выполнения команд` — category: architecture

### Возможности (architecture)

- **README-004** — [README.md:15] `.NET 10 background service с long-polling. Авторизованным пользователям доступны:` — category: architecture
- **README-005** — [README.md:17] `Навигация по файловой системе и выбор RVT-файлов через inline-клавиатуры` — category: architecture
- **README-006** — [README.md:18] `Экспорт: PDF, DWG, NWC, IFC` — category: command-code
- **README-007** — [README.md:19] `Автоматизация: BIM-документирование, Clash Reports, AutoResolve` — category: command-code
- **README-008** — [README.md:20] `Управление сессиями и командами через `/status`` — category: other
- **README-009** — [README.md:21] `Запрос доступа с подтверждением администратором` — category: architecture
- **README-010** — [README.md:22] `Дневной лимит файлов на пользователя` — category: architecture

### Технологии (dependency)

- **README-011** — [README.md:26] `.NET 10` (`net10.0`)` — category: dependency
- **README-012** — [README.md:27] `Telegram.Bot 22.10.0.1` — category: dependency
- **README-013** — [README.md:28] `PostgreSQL` (Npgsql + Dapper)` — category: dependency
- **README-014** — [README.md:29] `Serilog` (Console + Seq)` — category: dependency
- **README-015** — [README.md:30] `OpenMcdf` — чтение OLE-потоков .rvt/.rfa` — category: dependency
- **README-016** — [README.md:31] `Windows Registry` — поиск Revit/Navisworks` — category: dependency

### Требования (other)

- **README-017** — [README.md:35] `**Windows only** — использует Windows Registry и P/Invoke WinAPI.` — category: other
- **README-018** — [README.md:37] `PostgreSQL 15+` — category: dependency
- **README-019** — [README.md:38] `Docker` (рекомендуется для PostgreSQL)` — category: dependency

### Структура (architecture / class-name)

- **README-020** — [README.md:42] `4 проекта (`TelegramBot.slnx`):` — category: architecture
- **README-021** — [README.md:50] `└── TelegramBot.Worker └── BimLib/ (BIM-интеграция)` — category: architecture
- **README-022** — [README.md:55-58] `TelegramBot.Core | Модели, DTO, интерфейсы, константы; TelegramBot.Data | PostgreSQL persistence (Dapper); TelegramBot.Server | Telegram-инфраструктура, хендлеры, хостинг; TelegramBot.Worker | Фоновое выполнение задач + BimLib` — category: architecture
- **README-023** — [README.md:64] `| `TelegramBotHostedService` | Server | Polling-цикл, точка входа |` — category: class-name
- **README-024** — [README.md:65] `| `CommandAppService` | Server | Центральный диспетчер, проверка доступа |` — category: class-name
- **README-025** — [README.md:66] `| `SlashCommandService` | Server | Обработка текстовых команд |` — category: class-name
- **README-026** — [README.md:67] `| `CallbackDispatcher` | Server | Chain-of-responsibility маршрутизация callback-ов |` — category: class-name
- **README-027** — [README.md:68] `| `SessionManager` | Server | In-memory сессии (5 мин timeout) |` — category: class-name
- **README-028** — [README.md:69] `| `FileSystemBrowser` | Server | Навигация по файловой системе |` — category: class-name
- **README-029** — [README.md:70] `| `PostgresDataService` | Data | Вся работа с БД |` — category: class-name
- **README-030** — [README.md:71] `| `CommandExecutionService` | Worker | Polling очереди, выполнение Revit/Navisworks/AI |` — category: class-name
- **README-031** — [README.md:72] `| `RevitVersionDetector` | Worker/BimLib | Определение версии Revit по .rvt-файлу |` — category: class-name
- **README-032** — [README.md:73] `| `RevitPathResolver` | Worker/BimLib | Поиск Revit.exe через реестр |` — category: class-name
- **README-033** — [README.md:74] `| `DialogDismisser` | Worker/BimLib | Автозакрытие диалогов Revit |` — category: class-name

### Архитектура (architecture / class-name)

- **README-034** — [README.md:81-83] `Telegram API → TelegramBotHostedService → TelegramUpdateMapper → CommandAppService → SlashCommandService / CallbackDispatcher` — category: architecture
- **README-035** — [README.md:88-90] `Server создаёт Commands (Status='pending') → PostgreSQL NOTIFY new_tasks → Worker CLAIM (FOR UPDATE SKIP LOCKED) → выполнение → UPDATE Status='Done'/'Failed' → NOTIFY command_completed → Server шлёт сводку пользователю` — category: architecture
- **README-036** — [README.md:93] `Поддерживается несколько Worker-ов (competing consumers).` — category: architecture

### Команды бота (other)

- **README-037** — [README.md:99] `| `/start` | Регистрация, запрос доступа |` — category: other
- **README-038** — [README.md:100] `| `/export` | Меню экспорта (PDF/DWG/NWC/IFC) |` — category: other
- **README-039** — [README.md:101] `| `/automation` | Меню автоматизации (BIMDOC/CLASHREP/AUTORES) |` — category: other
- **README-040** — [README.md:102] `| `/status` | Глобальный просмотр всех сессий и управление ими |` — category: other
- **README-041** — [README.md:103] `| `/help` | Справка |` — category: other

### Конфигурация (config-key)

- **README-042** — [README.md:111] `| `TelegramBot:Token` | Токен бота (или `TelegramBot__Token`) |` — category: config-key
- **README-043** — [README.md:112] `| `TelegramBot:AdminUserIds` | ID администраторов |` — category: config-key
- **README-044** — [README.md:113] `| `FileSystem:RootPath` | Корневая директория для навигации |` — category: config-key
- **README-045** — [README.md:114] `| `ConnectionStrings:Postgres` | PostgreSQL connection string |` — category: config-key
- **README-046** — [README.md:118-125] Пример `appsettings.Local.json` (gitignored) с ключами `TelegramBot:Token`, `TelegramBot:AdminUserIds`, `FileSystem:RootPath` — category: config-key
- **README-047** — [README.md:128] `Полный список параметров — см. `appsettings.json` в проектах Server и Worker.` — category: other

### База данных (sql-table / architecture)

- **README-048** — [README.md:134] `| `BotUsers` | Пользователи (роли, статусы доступа) |` — category: sql-table
- **README-049** — [README.md:135] `| `Sessions` | Сессии пользователей |` — category: sql-table
- **README-050** — [README.md:136] `| `Commands` | Команды внутри сессии (pending → processing → Done/Failed) |` — category: sql-table
- **README-051** — [README.md:137] `| `TrackedMessages` | Отслеживание сообщений Telegram |` — category: sql-table
- **README-052** — [README.md:139] `Soft-delete только — `Status = 'Deleted'`, никогда `DELETE FROM`.` — category: architecture

### Запуск (build-cmd / run-cmd)

- **README-053** — [README.md:145] `dotnet build TelegramBot.slnx` — category: build-cmd
- **README-054** — [README.md:148] `dotnet run --project TelegramBot.Server/TelegramBot.Server.csproj` — category: run-cmd
- **README-055** — [README.md:151] `dotnet run --project TelegramBot.Worker/TelegramBot.Worker.csproj` — category: run-cmd

### Docker (other)

- **README-056** — [README.md:160-163] Docker: `docker run -d --name telegram-bot-db -e POSTGRES_DB=telegram_bot -e POSTGRES_PASSWORD=postgres -p 5432:5432 postgres:17` — category: other
- **README-057** — [README.md:166-167] `docker build -t telegram-bot-server -f Dockerfile .` / `docker run --rm telegram-bot-server` — category: other
- **README-058** — [README.md:165] `Server (Windows-контейнер)` — category: other

### Безопасность (other)

- **README-059** — [README.md:172] `Пользователи со статусом `Pending` ждут подтверждения администратором` — category: other
- **README-060** — [README.md:173] `Администраторы (из `AdminUserIds`) автоматически получают `Approved`` — category: other
- **README-061** — [README.md:174] `Все `Approved` видят **все сессии** всех пользователей через `/status`` — category: other
- **README-062** — [README.md:175] `Токен хранится в `appsettings.Local.json` или переменной окружения` — category: other

### История (other)

- **README-063** — [README.md:178] `История изменений — см. ROADMAP.md.` — category: other

---

## ROADMAP.md (257 строк)

### Версия 1.0 (architecture / sql-table / class-name)

- **ROADMAP-001** — [ROADMAP.md:3] `Актуально на: 8 июня 2026` — category: other
- **ROADMAP-002** — [ROADMAP.md:10] `Модели, DTO, интерфейсы, конфигурация — нулевая зависимость от Telegram SDK` — category: architecture
- **ROADMAP-003** — [ROADMAP.md:11] `Архитектура с 4 проектами: `Core → Data → Server`, `Worker`` — category: architecture
- **ROADMAP-004** — [ROADMAP.md:12] `DI-регистрация всех сервисов как Singleton` — category: architecture
- **ROADMAP-005** — [ROADMAP.md:13] `PostgreSQL persistence через Dapper + Npgsql` — category: dependency
- **ROADMAP-006** — [ROADMAP.md:14] `Система доступа: регистрация → запрос → подтверждение администратором` — category: architecture
- **ROADMAP-007** — [ROADMAP.md:15] `Soft-delete для всех сущностей` — category: architecture
- **ROADMAP-008** — [ROADMAP.md:16] `.editorconfig` с правилами именования и форматирования` — category: other
- **ROADMAP-009** — [ROADMAP.md:19] `Long-polling через `TelegramBotHostedService` (BackgroundService)` — category: class-name
- **ROADMAP-010** — [ROADMAP.md:20] `Обработка текстовых команд: `/start`, `/help`, `/export`, `/automation`, `/status`` — category: other
- **ROADMAP-011** — [ROADMAP.md:21] `Chain of Responsibility для callback-хендлеров (7 хендлеров)` — category: class-name
- **ROADMAP-012** — [ROADMAP.md:22] `Навигация по файловой системе через inline-клавиатуры` — category: architecture
- **ROADMAP-013** — [ROADMAP.md:23] `Выбор проектов/секций/RVT-файлов` — category: architecture
- **ROADMAP-014** — [ROADMAP.md:24] `Управление сессиями и командами` — category: architecture
- **ROADMAP-015** — [ROADMAP.md:25] `Markdown-экранирование (MarkdownV2 + Markdown)` — category: other
- **ROADMAP-016** — [ROADMAP.md:27] `Команды экспорта: PDF, DWG, NWC, IFC` — category: command-code
- **ROADMAP-017** — [ROADMAP.md:28] `Команды автоматизации: BIMDOC, CLASHREP, AUTORES` — category: command-code
- **ROADMAP-018** — [ROADMAP.md:31] `Polling очереди команд раз в 1 минуту` — category: architecture
- **ROADMAP-019** — [ROADMAP.md:32] `Пул процессов (глобальный SemaphoreSlim)` — category: class-name
- **ROADMAP-020** — [ROADMAP.md:33] `Lease-механизм (TTL)` — category: architecture
- **ROADMAP-021** — [ROADMAP.md:34] `Таймаут выполнения процесса` — category: architecture
- **ROADMAP-022** — [ROADMAP.md:35] `Трекинг PID (ConcurrentDictionary + БД)` — category: class-name
- **ROADMAP-023** — [ROADMAP.md:36] `FOR UPDATE SKIP LOCKED — конкурентная обработка несколькими воркерами` — category: architecture
- **ROADMAP-024** — [ROADMAP.md:37] `Graceful shutdown исключён из требований — внешние процессы покрываются timeout/lease/crash recovery` — category: architecture
- **ROADMAP-025** — [ROADMAP.md:38] `Повторная проверка очереди после временных ошибок batch-а` — category: architecture
- **ROADMAP-026** — [ROADMAP.md:39] `Приоритеты команд (`Priority ASC, CreatedAt ASC, CommandId ASC`)` — category: architecture
- **ROADMAP-027** — [ROADMAP.md:40] `Поля `StartedAt`, `CompletedAt`, `ProcessId`, `ErrorMessage`` — category: sql-table
- **ROADMAP-028** — [ROADMAP.md:41] `Трекинг сообщений бота` — category: architecture
- **ROADMAP-029** — [ROADMAP.md:44] `SQL-запросы, разбитые по сущностям (5 partial-файлов)` — category: namespace
- **ROADMAP-030** — [ROADMAP.md:45] `Инициализация таблиц при старте` — category: architecture
- **ROADMAP-031** — [ROADMAP.md:46] `Сид администраторов` — category: architecture
- **ROADMAP-032** — [ROADMAP.md:47] `Индексы для производительности` — category: sql-table

### Версия 1.1 (architecture)

- **ROADMAP-033** — [ROADMAP.md:53] `Lease с долгим TTL — при захвате команды Lease = ProcessTimeoutSeconds + 5 мин.` — category: architecture
- **ROADMAP-034** — [ROADMAP.md:54] `Фоновая очистка каждые 60 сек возвращает команды с истёкшим Lease.` — category: architecture
- **ROADMAP-035** — [ROADMAP.md:56] `Асинхронное чтение stdout/stderr — `BeginOutputReadLine` / `BeginErrorReadLine`.` — category: class-name
- **ROADMAP-036** — [ROADMAP.md:57] `Вывод собирается в `StringBuilder` через событийные хендлеры.` — category: class-name
- **ROADMAP-037** — [ROADMAP.md:58] `stdout → Information, stderr → Warning. Обрезка >4KB для защиты от раздувания логов.` — category: other
- **ROADMAP-038** — [ROADMAP.md:60] `Валидация FilePath — проверка существования файла, расширения (из `AllowedExtensions`), защита от path traversal (`Path.GetFullPath()`).` — category: class-name
- **ROADMAP-039** — [ROADMAP.md:62-67] `Приоритетные партиции (priority-based) — SortedDictionary<int, SemaphoreSlim>: Critical (1, 3), High (2, 5), Medium (3, 3), Low (4, 1), Lowest (5+, 1)` — category: architecture
- **ROADMAP-040** — [ROADMAP.md:68-69] `Маршрутизация: первый partition threshold `>= Priority`, иначе последний threshold. Пороги: [1, 2, 3, 4, 5]. Чем меньше Priority, тем выше приоритет` — category: architecture
- **ROADMAP-041** — [ROADMAP.md:71] `Конфигурация через `WorkerOptions.Partitions` + appsettings.json` — category: config-key
- **ROADMAP-042** — [ROADMAP.md:72] `Retry logic — экспоненциальная задержка (`base * 2^(attempt-1)`): 60s, 120s, 240s, ... Лимит: `MaxRetries=5` Команда возвращается в `pending` с `NextRetryAt`..` — category: architecture
- **ROADMAP-043** — [ROADMAP.md:74] `Telegram-уведомления — о завершении/ошибках команд через отдельный канал LISTEN/NOTIFY (`command_completed`).` — category: architecture
- **ROADMAP-044** — [ROADMAP.md:75] `CommandNotificationService` слушает и отправляет сообщения.` — category: class-name
- **ROADMAP-045** — [ROADMAP.md:76] `Уведомления приходят только при завершении всей сессии (сводка: `N ✅, M ❌`)` — category: architecture
- **ROADMAP-046** — [ROADMAP.md:78] `Координация очистки Lease — `pg_try_advisory_lock(1234567)` перед каждой очисткой.` — category: architecture
- **ROADMAP-047** — [ROADMAP.md:80] `Primary constructors — миграция сервисов на C# 12 (TelegramBotHostedService, CallbackDispatcher, CommandExecutionService, CommandNotificationService и др.)` — category: class-name
- **ROADMAP-048** — [ROADMAP.md:82] `Рефакторинг навигации — удалён `PathMap`/`TryResolvePath`, передача путей напрямую в callback-данных вместо токенов. Упрощение `FileSystemBrowser`, `FileNavigationHandler`, `FileSelectionHandler`..` — category: class-name

### Версия 1.2 (architecture / class-name / config-key / sql-table)

- **ROADMAP-049** — [ROADMAP.md:91] `BimLib (встроен в Worker) — библиотека для определения версии Revit, резолвинга Revit.exe и мониторинга процессов` — category: architecture
- **ROADMAP-050** — [ROADMAP.md:92] `Определение версии Revit по .rvt-файлу: чтение OLE-потока BasicFileInfo через OpenMcdf, поиск строки `Format: YYYY`` — category: class-name
- **ROADMAP-051** — [ROADMAP.md:93] `Автоматический выбор Revit.exe: поиск пути через реестр Windows (`HKLM\SOFTWARE\Autodesk\Revit\{version}`) с fallback на WOW6432Node` — category: class-name
- **ROADMAP-052** — [ROADMAP.md:94] `Мониторинг здоровья процесса: проверка отклика, автозакрытие диалогов Revit` — category: class-name
- **ROADMAP-053** — [ROADMAP.md:95] `Поддержка Navisworks: поиск Navisworks.exe/FileConvert.exe через реестр Windows, мониторинг процессов (Roamer, FileConvert)` — category: class-name
- **ROADMAP-054** — [ROADMAP.md:96] `Graceful shutdown не нужен — при остановке Worker не реализует отдельное ожидание или завершение Revit/Navisworks. Корректность обеспечивают timeout, lease/crash recovery и повторный захват команд после перезапуска.` — category: architecture
- **ROADMAP-055** — [ROADMAP.md:98] `Расширенное логирование Revit-специфичных ошибок — отдельный файл BimLib.log (`~/Documents/TelegramBot/Logs/Worker/BimLib/log-.txt`), фильтрация через BimLibLogFilter по SourceContext "TelegramBot.BimLib.*"` — category: class-name
- **ROADMAP-056** — [ROADMAP.md:101] `Rate limiting — ограничение на количество команд от одного пользователя в единицу времени (sliding window per-user).` — category: architecture
- **ROADMAP-057** — [ROADMAP.md:103-104] `ProjectName в БД — колонка `ProjectName TEXT` в таблице `Sessions`. Имя проекта отображается в `/status` и в уведомлениях о завершении.` — category: sql-table
- **ROADMAP-058** — [ROADMAP.md:105-106] `Список ошибочных файлов в уведомлении — при наличии ошибок уведомление содержит список файлов с ошибками: `\n\nОшибки:\n- file.rvt`.` — category: architecture
- **ROADMAP-059** — [ROADMAP.md:107-108] `Timing stats в уведомлениях — уведомление о завершении содержит длительность сессии, рассчитанную по `MIN(StartedAt)` / `MAX(CompletedAt)` из таблицы `Commands`.` — category: architecture
- **ROADMAP-060** — [ROADMAP.md:109-111] `Оптимизация: in-memory счётчик сессий — удалён per-command `GetSessionProgressAsync`, заменён на `ConcurrentDictionary.AddOrUpdate`. Счётчик используется как batch-local оптимизация, а финальность сессии подтверждается БД через отсутствие `pending`/`processing`..` — category: class-name
- **ROADMAP-061** — [ROADMAP.md:112-113] `Исправлен retry/counter bug — retry теперь завершает текущий claim и декрементит `_sessionRemaining`; уведомление не теряется после повторных попыток.` — category: architecture
- **ROADMAP-062** — [ROADMAP.md:114-115] `Дневной лимит файлов на пользователя — `RateLimit:MaxFilesPerUserPerDay` ограничивает количество файлов, которые пользователь может поставить в очередь за 24 часа (`0` отключает лимит).` — category: config-key
- **ROADMAP-063** — [ROADMAP.md:116-117] `Автоочистка старых сессий — Worker мягко удаляет неактивные сессии старше `Worker:CompletedSessionRetentionDays`, если в них нет `pending`/`processing` команд (`0` отключает).` — category: config-key
- **ROADMAP-064** — [ROADMAP.md:118-119] `Confirmation dialogs для удаления — кнопки удаления сессии/команды сначала показывают подтверждение через `CONFIRMDELETESESSION:` / `CONFIRMDELETECOMMAND:`.` — category: callback-prefix
- **ROADMAP-065** — [ROADMAP.md:120-121] `Удалён мёртвый код — `GetCommandStatusAsync` (interface + implementation + SQL), `GetFailedFilesBySession` (не использовался — inline SQL вместо константы).` — category: class-name

### В планах / Открытые вопросы / Не планируется (other)

- **ROADMAP-066** — [ROADMAP.md:124] Опционально: Revit Journal-автоматизация — запуск сценариев через journal-файлы` — category: other
- **ROADMAP-067** — [ROADMAP.md:125-127] Интеграция Prometheus/Grafana — метрики в `CommandExecutionService` и `CommandNotificationService` (В ПЛАНАХ)` — category: other
- **ROADMAP-068** — [ROADMAP.md:128-129] Статистика выполнения — среднее время выполнения, процент успеха/ошибок по типам команд, по пользователям (В ПЛАНАХ)` — category: other
- **ROADMAP-069** — [ROADMAP.md:132] Открытый вопрос: Prometheus/Grafana — нужен отдельный HTTP exporter или достаточно периодических SQL-запросов?` — category: other
- **ROADMAP-070** — [ROADMAP.md:138] Открытый вопрос: Статус `Sessions` — нужно ли переводить `Sessions.Status` в `Done`/`Failed`?` — category: other
- **ROADMAP-071** — [ROADMAP.md:146] `Health checks для Worker — удалено из roadmap: сейчас не используется Docker/K8s, поэтому отдельные `/health`, `/healthz`, `/readyz` не нужны.` — category: other
- **ROADMAP-072** — [ROADMAP.md:147-148] `new_command LISTEN/NOTIFY` для Worker — по текущему решению не требуется; Worker использует polling очереди раз в минуту. `LISTEN/NOTIFY` остаётся только для `command_completed` уведомлений Server-а.` — category: other

### v1.3 (architecture / other)

- **ROADMAP-073** — [ROADMAP.md:152] `v1.3 — Упрощение алгоритма и кодовой базы (в планах)` — category: other
- **ROADMAP-074** — [ROADMAP.md:159] `Исправить критические ошибки — устранить утечки ресурсов, баги и другие проблемы` — category: other
- **ROADMAP-075** — [ROADMAP.md:161] `Единая модель жизненного цикла команды — явно описать допустимые переходы статусов (`pending → processing → done/failed/deleted`)` — category: other
- **ROADMAP-076** — [ROADMAP.md:164] `Свести retry, lease и timeout к одному понятному сценарию` — category: other
- **ROADMAP-077** — [ROADMAP.md:167] `Упростить уведомления о завершении сессии — оставить один источник истины для определения финальности` — category: other
- **ROADMAP-078** — [ROADMAP.md:170] `Пересмотреть in-memory счётчик сессий — оставить его только как оптимизацию; корректность завершения должна подтверждаться БД` — category: other
- **ROADMAP-079** — [ROADMAP.md:172] `Синхронизировать модель очереди с кодом — Worker не использует `new_command LISTEN/NOTIFY`; основной контур — polling раз в минуту, `command_completed` остаётся каналом уведомлений Server-а.` — category: other
- **ROADMAP-080** — [ROADMAP.md:176] `Разделить `CommandExecutionService` на небольшие компоненты — отдельно claim/lease, execution, retry/fail handling, notification trigger` — category: other
- **ROADMAP-081** — [ROADMAP.md:179] `Свести SQL-операции к сценарным методам — методы Data-слоя должны отражать бизнес-действия (`ClaimPendingCommandsAsync`, `MarkCommandCompletedAsync`, `ScheduleRetryAsync`)` — category: other
- **ROADMAP-082** — [ROADMAP.md:182] `Удалить оставшиеся мёртвые и исторические ветки` — category: other
- **ROADMAP-083** — [ROADMAP.md:185] `Упростить callback-хендлеры статуса и удаления` — category: other
- **ROADMAP-084** — [ROADMAP.md:188-189] `Синхронизировать документацию с реальным алгоритмом — обновить `Docs/execution-algorithm.md`, `README.md` и `AGENTS.md` после упрощения кода.` — category: other

### v1.2 рефакторинг (уже завершено) (class-name)

- **ROADMAP-085** — [ROADMAP.md:197] `DB-трекинг сообщений сохранён — `TrackedMessages` и методы `IDataService` используются для очистки сообщений` — category: class-name
- **ROADMAP-086** — [ROADMAP.md:198] `Удалены лишние интерфейсы — Удалены `IFileSystemBrowser`, `ITelegramUpdateMapper`, `IRevitPathResolver`, `IRevitProcessTracker`, `INavisworksProcessTracker` — прямые зависимости без потери тестируемости` — category: class-name
- **ROADMAP-087** — [ROADMAP.md:199] `Primary constructors — удалены redundant поля. Из 8 классов удалены ~23 redundant `private readonly` поля, дублирующих параметры primary constructor` — category: other
- **ROADMAP-088** — [ROADMAP.md:200] `CallbackHandlerBase — убрано двойное логирование. `HandleAsync()` больше не ловит исключения — только `CallbackDispatcher`.` — category: other
- **ROADMAP-089** — [ROADMAP.md:201] `Unused usings — `dotnet format --diagnostics IDE0005` удалил все неиспользуемые `using` directives по всему проекту` — category: build-cmd
- **ROADMAP-090** — [ROADMAP.md:203] `HandlerHelpers.SendActionsReplyKeyboardAsync() — Заменяет 3 дублированных метода в `FileNavigationHandler`, `CommandSelectionHandler`, `SlashCommandService`` — category: class-name
- **ROADMAP-091** — [ROADMAP.md:204] `ProcessHealthHelper.CheckHealth() — Общая логика для `RevitProcessTracker` и `NavisworksProcessTracker`` — category: class-name
- **ROADMAP-092** — [ROADMAP.md:205-206] `NpgsqlHelper.CreateOpenConnectionAsync() — Перенесён из `Worker.Services` (internal) → `TelegramBot.Data` (public). Используется в `CommandExecutionService` (Worker) и `CommandNotificationService` (Server)` — category: class-name
- **ROADMAP-093** — [ROADMAP.md:207] `TryParseId() — Заменяет 5 одинаковых блоков `int.TryParse` в `SessionManagementHandler`` — category: class-name
- **ROADMAP-094** — [ROADMAP.md:208] `Упрощение DI — `TelegramOutputService` больше не зависит от `IDataService`; убраны 2 лишних параметра из `TelegramBotHostedService`; мёртвый `IDataService` убран из `CommandAppService`` — category: class-name
- **ROADMAP-095** — [ROADMAP.md:209] `PostgresDataService — `CreateConnectionAsync()` — Выделен helper, заменивший ~15 ручных `new NpgsqlConnection + OpenAsync`` — category: class-name
- **ROADMAP-096** — [ROADMAP.md:210] `Документация — `AGENTS.md`, `ROADMAP.md` обновлены под все изменения` — category: other

### Рекомендации (other)

- **ROADMAP-097** — [ROADMAP.md:221] `Pre-warm Revit — Запуск Revit.exe занимает 1–10 минут. Решение: держать пул idle Revit-процессов, передавать файлы уже в запущенный Revit через API/Journal.` — category: other
- **ROADMAP-098** — [ROADMAP.md:222] `Timing stats в уведомлениях — ✅ Реализовано` — category: other
- **ROADMAP-099** — [ROADMAP.md:223] `Фильтрация в /status — При большом количестве сессий список становится нечитаемым. Решение: фильтрация по проекту (`ProjectName`) и статусу, пагинация по страницам` — category: other
- **ROADMAP-100** — [ROADMAP.md:224] `Дневной лимит файлов на пользователя — ✅ Реализовано. `RateLimit:MaxFilesPerUserPerDay = 100`; при превышении бот отказывает в создании новой сессии` — category: config-key
- **ROADMAP-101** — [ROADMAP.md:225] `Автоочистка старых сессий — ✅ Реализовано. Worker мягко удаляет сессии старше `CompletedSessionRetentionDays`, если в них нет `pending`/`processing`` — category: config-key
- **ROADMAP-102** — [ROADMAP.md:226] `Умный retry: transient vs permanent — Сейчас retry для всех ошибок одинаков. Решение: классифицировать по exit code / error message` — category: other
- **ROADMAP-103** — [ROADMAP.md:227] `Confirmation dialogs для удаления — ✅ Реализовано. `DELETESESSION:` / `DELETECOMMAND:` сначала показывают подтверждение, затем soft-delete` — category: callback-prefix

### Легенда (other)

- **ROADMAP-104** — [ROADMAP.md:243] `v1.0 ✅ Реализовано в базовой версии` — category: other
- **ROADMAP-105** — [ROADMAP.md:244] `v1.1 🟢 Реализовано (улучшения надёжности)` — category: other
- **ROADMAP-106** — [ROADMAP.md:245] `v1.2 🟢 Частично реализовано, оставшиеся пункты в планах` — category: other
- **ROADMAP-107** — [ROADMAP.md:246] `v1.3 🟡 В планах: упрощение алгоритма и кодовой базы` — category: other
- **ROADMAP-108** — [ROADMAP.md:247] `v2.0+ ⚪ Долгосрочные планы` — category: other

### Связанные документы (other)

- **ROADMAP-109** — [ROADMAP.md:254-257] Связанные документы: Docs/execution-algorithm.md, README.md, AGENTS.md, Docs/qodana-setup.md` — category: other

---

## Docs/execution-algorithm.md (1431 строка)

### Архитектурные паттерны (architecture / class-name)

- **DOCS-EXEC-001** — [Docs/execution-algorithm.md:33] `Chain of Responsibility — Обработка callback-запросов (`CallbackDispatcher`). Каждый хендлер проверяет, может ли он обработать callback. Если нет — передаёт следующему` — category: class-name
- **DOCS-EXEC-002** — [Docs/execution-algorithm.md:34] `Strategy — Исполнение команд (`CommandConfig`). Конфигурация команды определяет, какую стратегию запуска применить (Revit, Navisworks, Python)` — category: class-name
- **DOCS-EXEC-003** — [Docs/execution-algorithm.md:35] `Competing Consumers — Параллельная обработка (FOR UPDATE SKIP LOCKED). Несколько Worker-ов конкурируют за команды, каждая выполняется ровно одним` — category: architecture
- **DOCS-EXEC-004** — [Docs/execution-algorithm.md:36] `Polling — Очередь задач (Task.Delay). Worker просыпается каждую минуту для проверки новых команд. Server получает уведомления через `command_completed`` — category: architecture
- **DOCS-EXEC-005** — [Docs/execution-algorithm.md:37] `Bulkhead (изоляция) — Priority-based партиции (`SemaphoreSlim`). Каждый уровень приоритета имеет изолированный пул слотов` — category: architecture
- **DOCS-EXEC-006** — [Docs/execution-algorithm.md:39] `Retry with Exponential Backoff — Повторные попытки (`MaxRetries=5`). Задержка растёт экспоненциально: 60s → 120s → 240s → 480s → 960s` — category: architecture
- **DOCS-EXEC-007** — [Docs/execution-algorithm.md:40] `Lease (аренда) — Защита от сбоев воркеров (`Lease` + `StartedAt`). Команда «арендуется» на время выполнения; при сбое воркера возвращается в очередь` — category: architecture
- **DOCS-EXEC-008** — [Docs/execution-algorithm.md:41] `Soft Delete — Логическое удаление (`Status = 'Deleted'`). Строки никогда не удаляются физически` — category: architecture
- **DOCS-EXEC-009** — [Docs/execution-algorithm.md:42] `Singleton — DI-регистрация всех сервисов. Гарантирует единый экземпляр сервиса на всё приложение` — category: architecture

### Ключевые концепции (architecture)

- **DOCS-EXEC-010** — [Docs/execution-algorithm.md:56] `Отмена команд — любой одобренный пользователь может отменить команду через `/status` → кнопка «⛔ Отменить»; Server мягко удаляет команду (`Status = 'Deleted'`). Все одобренные пользователи могут удалять чужие сессии и команды.` — category: architecture

### Priority-based партиции (architecture)

- **DOCS-EXEC-011** — [Docs/execution-algorithm.md:121-125] `Priority 1 → Critical, SemaphoreSlim(3); Priority 2 → High, SemaphoreSlim(5); Priority 3 → Medium, SemaphoreSlim(3); Priority 4 → Low, SemaphoreSlim(1); Priority 5+ → Lowest, SemaphoreSlim(1)` — category: architecture
- **DOCS-EXEC-012** — [Docs/execution-algorithm.md:130-133] `Высокоприоритетные команды (Priority=1) имеют выделенные слоты и не ждут за низкоприоритетными. Гарантированная пропускная способность для критических задач. Low-priority команды не блокируют High-priority (даже если очередь забита)` — category: architecture

### BimLib (architecture / class-name / namespace / dependency)

- **DOCS-EXEC-013** — [Docs/execution-algorithm.md:138] `BimLib — **Windows-only** набор модулей, расположенный внутри Worker-проекта (`TelegramBot.Worker/BimLib/`).` — category: architecture
- **DOCS-EXEC-014** — [Docs/execution-algorithm.md:139] `Используется `CommandExecutionService` при выполнении Revit/Navisworks-команд.` — category: class-name
- **DOCS-EXEC-015** — [Docs/execution-algorithm.md:156-167] `Services/: RevitVersionDetector, RevitPathResolver, NavisworksPathResolver; Monitor/: RevitProcessTracker, NavisworksProcessTracker, DialogDismisser, ProcessHealthHelper, WindowUtil, WindowInfo; Native/ — P/Invoke WinAPI (User32, Win32Consts); Interfaces/ — 2 интерфейса: IRevitVersionDetector, INavisworksPathResolver; Models/ — RevitDetectedVersion, RevitProcessHealth; Config/ — BimIntegrationOptions` — category: class-name
- **DOCS-EXEC-016** — [Docs/execution-algorithm.md:177-180] `BimLib — не отдельный проект. Это директория внутри Worker. Ранее существовавшие интерфейсы `IRevitPathResolver`, `IRevitProcessTracker`, `INavisworksProcessTracker` удалены — у них не было потребителей вне BimLib. DI-регистрация выполняется напрямую в `Worker/Program.cs` (без `AddBimIntegration()`).` — category: architecture
- **DOCS-EXEC-017** — [Docs/execution-algorithm.md:188] `RootStorage.OpenRead() → OpenStream("BasicFileInfo") → извлечение "Format: YYYY"` — category: dependency
- **DOCS-EXEC-018** — [Docs/execution-algorithm.md:191] `HKLM\SOFTWARE\Autodesk\Revit\{version} → Revit.exe` — category: class-name
- **DOCS-EXEC-019** — [Docs/execution-algorithm.md:196] `RevitProcessTracker.CheckHealth(process) — Проверка Responding + автозакрытие диалогов через DialogDismisser` — category: class-name
- **DOCS-EXEC-020** — [Docs/execution-algorithm.md:203-204] `IRevitVersionDetector.DetectVersionAsync(filePath); INavisworksPathResolver.GetInstalledVersions(), ResolveNavisworksPath(year), ResolveFileConvertPath(year)` — category: class-name
- **DOCS-EXEC-021** — [Docs/execution-algorithm.md:210-215] `services.AddSingleton<IRevitVersionDetector, RevitVersionDetector>(); services.AddSingleton<RevitPathResolver>(); services.AddSingleton<RevitProcessTracker>(); services.AddSingleton<DialogDismisser>(); services.AddSingleton<INavisworksPathResolver, NavisworksPathResolver>(); services.AddSingleton<NavisworksProcessTracker>();` — category: architecture
- **DOCS-EXEC-022** — [Docs/execution-algorithm.md:219-225] Требуется секция `BimIntegration` в `appsettings.json`: MinSupportedVersion=2018, MaxSupportedVersion=2026, RevitInstallRoot=`C:\\Program Files\\Autodesk`` — category: config-key
- **DOCS-EXEC-023** — [Docs/execution-algorithm.md:230] `BimLib помечена `[SupportedOSPlatform("windows")]` — работает только на Windows` — category: dependency
- **DOCS-EXEC-024** — [Docs/execution-algorithm.md:231] `OpenMcdf 3.x парсит OLE Structured Storage (.rvt). API: `RootStorage.OpenRead()` → `OpenStream()` → `stream.Read()`` — category: dependency
- **DOCS-EXEC-025** — [Docs/execution-algorithm.md:233] `P/Invoke — в `Native/User32.cs` (поиск окон, клики, закрытие диалогов)` — category: namespace
- **DOCS-EXEC-026** — [Docs/execution-algorithm.md:234] `RevitProcessStatus` содержит 3 значения: `Healthy`, `NotResponding`, `Error`` — category: class-name

### Жизненный цикл команды (architecture)

- **DOCS-EXEC-027** — [Docs/execution-algorithm.md:242-246] `pending (Команда создана и ожидает выполнения в очереди); processing (Команда захвачена воркером и выполняется (Lease установлен)); Done (Команда успешно завершена); Failed (Команда завершена с ошибкой); Deleted (Команда удалена (логическое удаление, soft-delete))` — category: architecture
- **DOCS-EXEC-028** — [Docs/execution-algorithm.md:248] `Статус `processing` устанавливается атомарно при захвате команды с использованием `SELECT ... FOR UPDATE SKIP LOCKED`.` — category: architecture
- **DOCS-EXEC-029** — [Docs/execution-algorithm.md:249] `Статус `Deleted` является финальным — Worker не должен перезаписывать мягко удалённую команду.` — category: architecture

### Алгоритм работы Worker (architecture / class-name)

- **DOCS-EXEC-030** — [Docs/execution-algorithm.md:257] `Инициализация per-partition пулов (`SortedDictionary<int, SemaphoreSlim>`) из конфигурации (`WorkerOptions.Partitions`)` — category: architecture
- **DOCS-EXEC-031** — [Docs/execution-algorithm.md:268-303] `Цикл: Очистка истёкших Lease (каждые 5 мин) → Мониторинг здоровья процессов (каждые 30 сек) → Ожидание уведомлений: LISTEN new_tasks + fallback polling (5 мин) → Захват pending-команд (до DefaultBatchSize=5)` — category: architecture
- **DOCS-EXEC-032** — [Docs/execution-algorithm.md:284] `Worker подписан на канал new_tasks, мгновенно реагирует` — category: architecture
- **DOCS-EXEC-033** — [Docs/execution-algorithm.md:285] `Fallback polling (Task.Delay) срабатывает раз в 5 мин` — category: architecture
- **DOCS-EXEC-034** — [Docs/execution-algorithm.md:308-310] `Захват pending-команд из БД (до DefaultBatchSize=5) — SELECT ... FOR UPDATE SKIP LOCKED; ORDER BY Priority ASC, CreatedAt ASC, CommandId ASC; Статус → 'processing', Lease = timestamp` — category: architecture
- **DOCS-EXEC-035** — [Docs/execution-algorithm.md:317-322] `Параллельная обработка с priority-based пулами: определение партиции по приоритету команды, ожидание слота в своей партиции, чем меньше Priority, тем выше приоритет` — category: architecture
- **DOCS-EXEC-036** — [Docs/execution-algorithm.md:328-338] `Выполнение одной команды: Валидация FilePath → Создать ProcessStartInfo → process.Start() → Сохранить в _activeProcesses → Статус: 'processing', ProcessId = PID → Асинхронное чтение stdout/stderr → WaitForExit с таймаутом → Логирование → Status = 'Done' или 'Failed' → _activeProcesses.Remove() + partitionPool.Release()` — category: architecture
- **DOCS-EXEC-037** — [Docs/execution-algorithm.md:362] `Thresholds кешируются по возрастанию: `[1, 2, 3, 4, 5]`` — category: architecture
- **DOCS-EXEC-038** — [Docs/execution-algorithm.md:363] `По умолчанию: Priority 1 → pool(3), Priority 2 → pool(5), Priority 3 → pool(3), Priority 4 → pool(1), Priority 5+ → pool(1)` — category: architecture
- **DOCS-EXEC-039** — [Docs/execution-algorithm.md:383] `private readonly ConcurrentDictionary<int, Process> _activeProcesses;` — category: class-name
- **DOCS-EXEC-040** — [Docs/execution-algorithm.md:395] `Process` хранится напрямую, без класса-обёртки. `Stopwatch` и `CommandId` — локальные переменные в `ExecuteOneAsync`.` — category: class-name
- **DOCS-EXEC-041** — [Docs/execution-algorithm.md:400] `При отмене команды пользователем Server устанавливает статус `Deleted` в БД. Отдельного промежуточного статуса отмены, per-command CTS и отдельного cancel-уведомления нет.` — category: architecture
- **DOCS-EXEC-042** — [Docs/execution-algorithm.md:402-408] `Процесс отмены: Пользователь нажимает «⛔ Отменить» в Telegram; Server обновляет статус команды на `Deleted` в БД; Worker не включает удалённую команду в выборку `ClaimPendingCommandsAsync`; Если команда уже в статусе `processing` (выполняется), она продолжит выполнение, но её результат (`Done`/`Failed`) не перезапишет soft-delete — `UpdateStatus` имеет защиту: `WHERE Status != 'Deleted'`` — category: architecture
- **DOCS-EXEC-043** — [Docs/execution-algorithm.md:414-416] `При потере соединения с базой данных: Зафиксировать ошибку в логе, Выждать паузу 5 сек, Восстановить подключение, Продолжить обработку очередей` — category: architecture

### Защита от зависаний и сбоев (architecture / class-name / sql-table)

- **DOCS-EXEC-044** — [Docs/execution-algorithm.md:430-451] `Lease-механизм: атомарный захват с Lease (TTL). Очистка истёкших Lease: каждые 5 минут + при старте воркера, Status = 'pending', Lease = NULL, StartedAt = NULL, ErrorMessage = 'Lease expired: worker crash or timeout'` — category: architecture
- **DOCS-EXEC-045** — [Docs/execution-algorithm.md:454-455] `Lease устанавливается на `ProcessTimeoutSeconds + 5 мин` (долгий TTL); CleanupIntervalSec = 300 (5 мин) — проверка каждые 5 минут (фоновая задача)` — category: architecture
- **DOCS-EXEC-046** — [Docs/execution-algorithm.md:465] `var timeout = TimeSpan.FromSeconds(ProcessTimeoutSec); // 3600 сек = 1 час` — category: other
- **DOCS-EXEC-047** — [Docs/execution-algorithm.md:480-488] `Дополнительная защита (SQL): каждые 5 минут — UPDATE Commands SET Status = 'pending', StartedAt = NULL, ProcessId = NULL, ErrorMessage = 'Timeout: process exceeded maximum execution time', WHERE Status = 'processing' AND StartedAt < NOW() - INTERVAL '@TimeoutSeconds seconds'` — category: sql-table
- **DOCS-EXEC-048** — [Docs/execution-algorithm.md:496-507] `_activeProcesses[cmd.CommandId] = process; _activeProcesses.TryRemove(cmd.CommandId, out _);` — category: class-name
- **DOCS-EXEC-049** — [Docs/execution-algorithm.md:516-531] `FOR UPDATE SKIP LOCKED — несколько воркеров могут работать параллельно. SQL: WITH selected AS (SELECT ... FOR UPDATE SKIP LOCKED) UPDATE Commands c SET Status = 'processing', Lease = @LeaseExpiry FROM selected WHERE c.CommandId = selected.CommandId RETURNING ...` — category: sql-table
- **DOCS-EXEC-050** — [Docs/execution-algorithm.md:575-580] `Валидация FilePath: путь не пустой, канонический путь не отличается от исходного (защита от `../` traversal), файл существует, расширение файла входит в `AllowedExtensions` (если указаны)` — category: class-name
- **DOCS-EXEC-051** — [Docs/execution-algorithm.md:589-600] `Outer retry loop для переподключения при потере связи с PostgreSQL: while (!stoppingToken.IsCancellationRequested) { try { await RunListenerLoopAsync(stoppingToken); } catch (Exception ex) { ... await Task.Delay(ReconnectDelayMs, stoppingToken); } }` — category: class-name

### Алгоритм работы Server (architecture)

- **DOCS-EXEC-052** — [Docs/execution-algorithm.md:614-621] `Определить приоритет команды на основе контекста (тип задачи, роль пользователя); Партиция вычисляется автоматически воркером из поля `Priority` (не задаётся на сервере); Создать сессию (если требуется); Вставить команду со статусом `pending`, указав приоритет; Все операции в одной транзакции` — category: architecture
- **DOCS-EXEC-053** — [Docs/execution-algorithm.md:625-626] `Worker забирает команды при получении уведомления `new_tasks` (мгновенно) или при fallback polling (до 5 мин). Worker подписан на `LISTEN new_tasks`, fallback polling — раз в 5 минут` — category: architecture

### Отмена команды пользователем (architecture / sql-table)

- **DOCS-EXEC-054** — [Docs/execution-algorithm.md:632-646] `В /status отображаются **все сессии всех пользователей** (глобальный статус), с указанием `[username]` рядом с каждой сессией. SQL: UPDATE Commands SET Status = 'Deleted' WHERE CommandId = @CommandId AND (SessionId IN (SELECT SessionId FROM Sessions WHERE UserId = @UserId) OR @IsAdmin = true)` — category: sql-table
- **DOCS-EXEC-055** — [Docs/execution-algorithm.md:650-652] `Ранее отмена включала отдельный промежуточный статус и отдельное cancel-уведомление. В текущей реализации отмена является soft-delete команды.` — category: architecture

### Уведомления пользователей (architecture / class-name / sql-table)

- **DOCS-EXEC-056** — [Docs/execution-algorithm.md:695] `NOTIFY command_completed, 'UserId|SessionId|Done|Total|ProjectName'` — category: architecture
- **DOCS-EXEC-057** — [Docs/execution-algorithm.md:698] `Формат: pipe-разделённые поля (`Split('|', 5)`)` — category: other
- **DOCS-EXEC-058** — [Docs/execution-algorithm.md:702-706] `Payload поля: UserId BIGINT, SessionId INT, Done INT, Total INT, ProjectName TEXT (пусто для старых сессий)` — category: sql-table
- **DOCS-EXEC-059** — [Docs/execution-algorithm.md:711-719] `Формат сообщения: ✅ ProjectA — сессия завершена — все 5 файлов обработано; ❌ ProjectA — сессия завершена — все 3 файлов с ошибками; ⚠️ ProjectA — сессия завершена: 3 ✅, 2 ❌ из 5; Ошибки:\n- model.rvt\n- another.rvt` — category: other
- **DOCS-EXEC-060** — [Docs/execution-algorithm.md:733-754] `In-memory счётчик сессий: ConcurrentDictionary<int, int> _sessionRemaining; При ClaimPendingCommandsAsync добавляем claimed-команды; При выходе команды из processing — CompleteClaimedCommandAsync декрементит счётчик; если newRemaining == 0 проверяет БД через CountPendingProcessingBySessionAsync; если 0 в БД — вызывает NotifyCommandCompletedAsync` — category: class-name
- **DOCS-EXEC-061** — [Docs/execution-algorithm.md:768-775] `Запрос длительности сессии: SELECT EXTRACT(EPOCH FROM (MAX(CompletedAt) - MIN(StartedAt)))::int FROM Commands WHERE SessionId = @SessionId AND Status != 'Deleted' AND StartedAt IS NOT NULL AND CompletedAt IS NOT NULL` — category: sql-table
- **DOCS-EXEC-062** — [Docs/execution-algorithm.md:779-782] `Запрос Failed-файлов: SELECT FilePath FROM Commands WHERE SessionId = @SessionId AND Status = 'Failed';` — category: sql-table
- **DOCS-EXEC-063** — [Docs/execution-algorithm.md:786] `Имена файлов извлекаются через `Path.GetFileName()` и добавляются в сообщение: \n\nОшибки:\n- model.rvt\n- another.rvt` — category: other
- **DOCS-EXEC-064** — [Docs/execution-algorithm.md:791-796] `Сторона Worker (`CommandExecutionService.CompleteClaimedCommandAsync`): Вызывается после `Done`, `Failed`, unknown/invalid command и после планирования retry; Декрементит in-memory счётчик; Если newRemaining == 0 — проверяет CountPendingProcessingBySessionAsync; Если в БД нет pending/processing — шлёт NotifyCommandCompletedAsync; Промежуточные команды и retry не отправляют пользовательских уведомлений` — category: class-name
- **DOCS-EXEC-065** — [Docs/execution-algorithm.md:799-804] `Сторона Server (`CommandNotificationService`): BackgroundService, подписан на LISTEN command_completed; При получении NOTIFY парсит payload через Split('|', 5); Запрашивает длительность сессии; Если failed > 0 — запрашивает Failed-файлы из БД; Отправляет сводку через ITelegramOutputService.SendMessageAsync(); Markdown-форматирование не используется (plain text)` — category: class-name

### Конфигурация (config-key)

- **DOCS-EXEC-066** — [Docs/execution-algorithm.md:812-822] `Параметры CommandExecutionService: Partitions {1→3, 2→5, 3→3, 4→1, 5→1}, ProcessTimeoutSeconds=10800 (3 часа), MaxRetries=5, RetryDelayBaseSeconds=60, CompletedSessionRetentionDays=30, CleanupIntervalSec=300, HealthCheckIntervalSec=30, FallbackTimeoutSec=300, ReconnectDelayMs=5000` — category: config-key
- **DOCS-EXEC-067** — [Docs/execution-algorithm.md:826-865] `appsettings.json Worker: ProcessTimeoutSeconds=10800, CompletedSessionRetentionDays=30, Partitions {1:3, 2:5, 3:3, 4:1, 5:1}, Commands: { PDF: Revit.exe /command "{CommandText}" "{FilePath}" [".rvt", ".rfa"]; DWG: Revit.exe ...; NWC: FileConvert.exe ...; AUTORES: python ai_agent.py --command "{CommandText}" --file "{FilePath}" [".rvt", ".ifc", ".nwc"] WorkingDirectory="." }` — category: config-key
- **DOCS-EXEC-068** — [Docs/execution-algorithm.md:868] `ProcessTimeoutSeconds` задаётся в секции `Worker`. Если не указан — по умолчанию 10800 сек (3 часа).` — category: config-key
- **DOCS-EXEC-069** — [Docs/execution-algorithm.md:873-878] `Приоритеты команд (CommandPriorityMap в SlashCommandService.cs): PDF=1 Critical, DWG=2 High, NWC/IFC/BIMDOC/CLASHREP=3 Medium, AUTORES=4 Low, Не указана=50 Lowest (fallback)` — category: class-name

### База данных (sql-table)

- **DOCS-EXEC-070** — [Docs/execution-algorithm.md:890] `В системе **4 таблицы**` — category: sql-table
- **DOCS-EXEC-071** — [Docs/execution-algorithm.md:894-897] `Таблицы: BotUsers (UserId, Status), Sessions (SessionId, UserId, CreatedAt), Commands (CommandId, SessionId, Status), TrackedMessages (MessageId, SessionId, ChatId, MessageIdPg)` — category: sql-table
- **DOCS-EXEC-072** — [Docs/execution-algorithm.md:954-963] `Soft-delete: мы **никогда** не удаляем строки из БД физически. Вместо `DELETE FROM Commands` мы пишем: UPDATE Commands SET Status = 'Deleted' WHERE ...` — category: architecture
- **DOCS-EXEC-073** — [Docs/execution-algorithm.md:967-974] `LISTEN/NOTIFY + fallback polling: Worker подписан на канал `new_tasks` через PostgreSQL `LISTEN/NOTIFY` и мгновенно реагирует на новые задачи. Fallback polling срабатывает раз в 5 минут при потере соединения` — category: architecture
- **DOCS-EXEC-074** — [Docs/execution-algorithm.md:990-994] `Lease — страховка от падения Worker. Когда Worker забирает команду, он говорит: «Я забрал эту команду. Если через 5 минут я не отвечу — значит, я упал, забирайте её обратно в очередь»` — category: architecture
- **DOCS-EXEC-075** — [Docs/execution-algorithm.md:1001-1018] `Таблица Commands поля: CommandId, SessionId, CommandText, FilePath, ExecutionOrder, Status, CreatedAt, StartedAt, CompletedAt, Lease (Unix-время в секундах), Priority (1-5, 1=наивысший, 50=default из CommandPriorityMap), Partition, ProcessId, ErrorMessage, RetryCount, NextRetryAt, Progress, Result` — category: sql-table
- **DOCS-EXEC-076** — [Docs/execution-algorithm.md:1026-1029] `Индексы: (Status, Priority, CreatedAt); (Status, Lease) WHERE Status = 'processing'; (SessionId); (UserId, CreatedAt DESC)` — category: sql-table

### SQL-операции (sql-table)

- **DOCS-EXEC-077** — [Docs/execution-algorithm.md:1037-1041] `Вставка команды: INSERT INTO "Commands" ("SessionId", "CommandText", "FilePath", "ExecutionOrder", "Priority") SELECT @SessionId, unnest(@CommandTexts::text[]), unnest(@FilePaths::text[]), unnest(@Orders::int[]), unnest(@Priorities::int[]);` — category: sql-table
- **DOCS-EXEC-078** — [Docs/execution-algorithm.md:1047-1066] `Захват команд (атомарный, с Lease): WITH selected AS (... FOR UPDATE SKIP LOCKED) UPDATE "Commands" c SET "Status" = 'processing', "Lease" = @LeaseExpiry, "StartedAt" = NOW() FROM selected WHERE c."CommandId" = selected."CommandId" RETURNING selected.CommandId, selected.SessionId, selected.CommandText, selected.FilePath, selected.ExecutionOrder, selected.UserId, selected.Username, selected.Partition, selected.Priority;` — category: sql-table
- **DOCS-EXEC-079** — [Docs/execution-algorithm.md:1071-1080] `Обновление статуса: UPDATE "Commands" SET "Status" = @Status, "CompletedAt" = CASE WHEN @Status IN ('Done', 'Failed') THEN NOW() ELSE "CompletedAt" END, "ProcessId" = @ProcessId, "ErrorMessage" = @ErrorMessage WHERE "CommandId" = @CommandId;` — category: sql-table
- **DOCS-EXEC-080** — [Docs/execution-algorithm.md:1085-1089] `NOTIFY command_completed, 'UserId|SessionId|Done|Total|ProjectName'; Payload генерируется в PostgresDataService.NotifyCommandCompletedAsync()` — category: class-name
- **DOCS-EXEC-081** — [Docs/execution-algorithm.md:1093-1103] `Очистка истёкших Lease (каждые 5 мин + при старте воркера): UPDATE "Commands" SET "Status" = 'pending', "Lease" = NULL, "StartedAt" = NULL, "ErrorMessage" = 'Lease expired: worker crash or timeout' WHERE "Status" = 'processing' AND "Lease" IS NOT NULL AND "Lease" < @CurrentTimeSec;` — category: sql-table
- **DOCS-EXEC-082** — [Docs/execution-algorithm.md:1107-1118] `Очистка команд по таймауту: UPDATE "Commands" SET "Status" = 'pending', ... WHERE "Status" = 'processing' AND "StartedAt" < NOW() - INTERVAL '@TimeoutSeconds seconds';` — category: sql-table
- **DOCS-EXEC-083** — [Docs/execution-algorithm.md:1122-1129] `Отмена команды пользователем: UPDATE Commands SET Status = 'Deleted' WHERE CommandId = @CommandId AND (SessionId IN (SELECT SessionId FROM Sessions WHERE UserId = @UserId) OR @IsAdmin = true);` — category: sql-table

### Безопасность и надёжность (architecture)

- **DOCS-EXEC-084** — [Docs/execution-algorithm.md:1138-1158] `Принципы: Логическое удаление; Транзакционность (FOR UPDATE SKIP LOCKED); Per-partition пулы; Приоритизация (Priority ASC); Lease-механизм; Таймауты (process.Kill(true)); Трекинг PID; Отказоустойчивость (5 сек задержка); Логирование stdout/stderr; Изоляция компонентов; Shutdown Worker — Graceful shutdown не нужен; FOR UPDATE SKIP LOCKED; Валидация FilePath; Асинхронное чтение stdout/stderr; Уведомления пользователей; Отмена команд; Автоочистка сессий; Очередь задач (LISTEN new_tasks + fallback)` — category: architecture

### Выполнение внешнего процесса (architecture / class-name)

- **DOCS-EXEC-085** — [Docs/execution-algorithm.md:1164-1183] `ExecuteOneAsync: Валидация FilePath → Поиск конфигурации WorkerOptions.Commands.TryGetValue(CommandText) → Создание ProcessStartInfo → Запуск process.Start() → Трекинг _activeProcesses[CommandId] = process → Статус UpdateCommandStatus(Processing, ProcessId=PID) → stdout/stderr → WaitForExit(ProcessTimeoutSeconds) → Логирование (обрезка >4KB) → Таймаут Kill(true) / ExitCode==0 Done / иначе Failed → Очистка partitionPool.Release() + _activeProcesses.TryRemove()` — category: architecture
- **DOCS-EXEC-086** — [Docs/execution-algorithm.md:1188-1194] `CommandConfig поля: ExecutablePath, ArgumentsTemplate ({CommandText}, {FilePath}), AllowedExtensions (null=любое), WorkingDirectory (null=папка файла, "." =корень)` — category: class-name
- **DOCS-EXEC-087** — [Docs/execution-algorithm.md:1197-1222] `CreateProcessStartInfo: FileName=cfg.ExecutablePath, Arguments=args (Replace), WorkingDirectory=..., RedirectStandardOutput=true, RedirectStandardError=true, UseShellExecute=false, CreateNoWindow=true, StandardOutputEncoding=UTF8, StandardErrorEncoding=UTF8` — category: class-name

### Расширение системы (architecture / other)

- **DOCS-EXEC-088** — [Docs/execution-algorithm.md:1229-1255] `Добавление новой команды: appsettings.json Commands → Приоритет в CommandPriorityMap в SlashCommandService.cs (default=50, попадёт в Lowest) → Партиции — пороги 1=Critical(3), 2=High(5), 3=Medium(3), 4=Low(1), 5=Lowest(1)` — category: architecture
- **DOCS-EXEC-089** — [Docs/execution-algorithm.md:1241] `Партиция не указывается в команде — определяется автоматически по полю `Priority` из БД.` — category: architecture
- **DOCS-EXEC-090** — [Docs/execution-algorithm.md:1259-1267] `Стандартные лимиты партиций: 1→3, 2→5, 3→3, 4→1, 5→1` — category: config-key

### Диагностика и мониторинг (sql-table)

- **DOCS-EXEC-091** — [Docs/execution-algorithm.md:1281-1373] `Диагностические запросы: pending-команды с приоритетами; активные выполнения с PID и длительностью; история за 24 часа; зависшие команды (Lease/StartedAt); статистика по статусам; удалённые команды; pg_listening_channels() для Server должен вернуть 'command_completed'` — category: sql-table
- **DOCS-EXEC-092** — [Docs/execution-algorithm.md:1368-1373] `PowerShell: Get-Process -Id <ProcessId> -ErrorAction SilentlyContinue; Get-Process Revit* | Select-Object Id, StartTime, CPU` — category: other

### Критерии корректной реализации (architecture)

- **DOCS-EXEC-093** — [Docs/execution-algorithm.md:1380-1392] `Критерии корректной реализации: 1) Лимит процессов (Critical=1→3, High=2→5, Medium=3→3, Low=4→1, Lowest=5+→1); 2) Приоритизация; 3) Lease-механизм; 4) Таймауты (3 часа default); 5) Трекинг PID; 6) FOR UPDATE SKIP LOCKED; 7) Shutdown Worker — graceful не реализуется; 8) Очередь задач (LISTEN new_tasks + 5 мин fallback); 9) Восстановление; 10) Наблюдаемость; 11) Отмена команд` — category: architecture

### Известные ограничения и технический долг (other / architecture)

- **DOCS-EXEC-094** — [Docs/execution-algorithm.md:1401-1412] `Тех. долг: DOC-001 Lease(5 мин)<ProcessTimeout(1 час) ✅ Исправлено (v1.1); DOC-002 stdout/stderr ✅ Исправлено (v1.1); DOC-003 Валидация FilePath ✅ Исправлено (v1.1); DOC-004 Партиции (priority-based) ✅ Реализовано (v1.1); DOC-005 Retry logic ✅ Реализовано (v1.2); DOC-006 Prometheus/Grafana В планах (v1.2); DOC-007 Координация очистки Lease ✅ Реализовано (v1.2); DOC-008 Graceful shutdown не нужен; DOC-009 Health checks Улучшение; DOC-010 Нет ограничения очереди Улучшение; DOC-011 Не описаны runbook Улучшение` — category: other
- **DOCS-EXEC-095** — [Docs/execution-algorithm.md:1418-1420] `Приоритеты исправлений: 🔴 HIGH (до production), 🟠 MEDIUM (v1.1-v1.2), 🟡 LOW (по мере доступности)` — category: other

### Связанные документы (other)

- **DOCS-EXEC-096** — [Docs/execution-algorithm.md:3] `Связанные документы: ROADMAP.md — дорожная карта проекта; README.md — обзор проекта` — category: other
- **DOCS-EXEC-097** — [Docs/execution-algorithm.md:1422-1431] `План работ → см. ROADMAP.md. v1.0 ✅, v1.1 ✅, v1.2 🟡, v2.0+ ⚪, Текущий спринт 🔄` — category: other

---

## Docs/CommandExecutionAlgorithm.md (407 строк)

### Заголовок / Участники (class-name)

- **DOCS-CEA-001** — [Docs/CommandExecutionAlgorithm.md:1] `# Алгоритм выполнения команд (Sequence Diagram)` — category: other
- **DOCS-CEA-002** — [Docs/CommandExecutionAlgorithm.md:3-4] `Текстовое представление диаграммы `CommandExecutionAlgorithm.puml`. Полная спецификация: execution-algorithm.md` — category: other
- **DOCS-CEA-003** — [Docs/CommandExecutionAlgorithm.md:12-17] `Участники: User; Server — TelegramBotHostedService — точка входа, polling; App — CommandAppService + SlashCommandService — логика команд; DB — PostgreSQL (очередь + LISTEN/NOTIFY); Worker — CommandExecutionService — фоновое выполнение; Process — Внешний процесс (Revit / Navisworks / Python)` — category: class-name

### Создание задачи (class-name)

- **DOCS-CEA-004** — [Docs/CommandExecutionAlgorithm.md:28-43] `Создание задачи: User → Server → App: ConfirmFileSelectionAsync() → App: CollectRvtFiles() → App: CreateSessionWithCommandsAsync() → DB: Batch INSERT (Session + Commands, Status='pending', Priority из CommandPriorityMap) → sessionId` — category: class-name

### Worker просыпается (architecture / class-name)

- **DOCS-CEA-005** — [Docs/CommandExecutionAlgorithm.md:55-60] `Worker: LISTEN new_tasks + fallback polling (5 мин); ProcessBatchAsync() — (внутренняя обработка)` — category: architecture

### Захват команд (architecture / sql-table / class-name)

- **DOCS-CEA-006** — [Docs/CommandExecutionAlgorithm.md:70-92] `ClaimPendingCommandsAsync(limit=DefaultBatchSize=5) → WITH selected AS (... FOR UPDATE SKIP LOCKED) UPDATE Commands c SET Status = 'processing', Lease = @LeaseExpiry, StartedAt = NOW() FROM selected WHERE c.CommandId = selected.Id RETURNING ...;` — category: sql-table
- **DOCS-CEA-007** — [Docs/CommandExecutionAlgorithm.md:80] `LIMIT 50` (в SELECT) — category: other

### Priority-based партиции (architecture)

- **DOCS-CEA-008** — [Docs/CommandExecutionAlgorithm.md:101-128] `ProcessWithPoolAsync(cmd) — определение партиции. Priority 1 → Critical → 3 слота; Priority 2 → High → 5 слотов; Priority 3 → Medium → 3 слота; Priority 4 → Low → 1 слот; Priority 5+ → Lowest → 1 слот. Чем меньше Priority, тем выше приоритет. await pool.WaitAsync(ct)` — category: architecture
- **DOCS-CEA-009** — [Docs/CommandExecutionAlgorithm.md:114-128] `Схема: Critical (≤1) — 3 процесса; High (≤2) — 5; Medium (≤3) — 3; Low (≤4) — 1; Lowest (≤5) — 1` — category: architecture

### Валидация FilePath (class-name)

- **DOCS-CEA-010** — [Docs/CommandExecutionAlgorithm.md:138-148] `ValidateFilePath(): 1. Path.GetFullPath() — защита от path traversal; 2. File.Exists() — файл существует?; 3. AllowedExtensions — расширение разрешено? Если ошибка → DB: UpdateCommandStatus(Failed). Если OK → продолжаем` — category: class-name

### Запуск процесса (class-name / architecture)

- **DOCS-CEA-011** — [Docs/CommandExecutionAlgorithm.md:158-183] `Запуск процесса: CreateProcessStartInfo() — ExecutablePath: "Revit.exe"; Arguments: "/command PDF \"file.rvt\""; WorkingDirectory: из конфига; RedirectStandardOutput/Error = true. process.Start() → UpdateCommandStatus(Processing, ProcessId=PID) → BeginOutputReadLine() + BeginErrorReadLine() — асинхронное чтение stdout/stderr в StringBuilder через событийные хендлеры — предотвращает deadlock при заполнении буфера 64KB` — category: class-name

### Завершение процесса (class-name)

- **DOCS-CEA-012** — [Docs/CommandExecutionAlgorithm.md:191-227] `process.WaitForExit(timeoutMs) — ожидание с таймаутом. Timeout → process.Kill(true) → UpdateCommandStatus(Failed, "Timeout..."). ExitCode == 0 (успех) → UpdateCommandStatus(Done) → NotifyCommandCompletedAsync() → NOTIFY command_completed. ExitCode != 0 (ошибка) → ScheduleRetryAsync(): Retry #1: +60s (base * 2^0); Retry #2: +120s (base * 2^1); Retry #3: +240s (base * 2^2); Retry #4: +480s (base * 2^3); Retry #5: +960s (base * 2^4); MaxRetries=5. UPDATE Status='pending', NextRetryAt=... Если все retry исчерпаны → UpdateCommandStatus(Failed, errorMessage) + NotifyCommandCompletedAsync() → NOTIFY command_completed` — category: class-name

### Уведомление пользователя (architecture / class-name)

- **DOCS-CEA-013** — [Docs/CommandExecutionAlgorithm.md:235-249] `DB: NOTIFY command_completed → Server: Parse payload (UserId|SessionId|Done|Total|ProjectName) → Server: Send Telegram message → User: ✅ ProjectA — сессия завершена — все 5 файлов обработано` — category: architecture

### Cleanup (class-name)

- **DOCS-CEA-014** — [Docs/CommandExecutionAlgorithm.md:258-264] `Worker: _activeProcesses.TryRemove(commandId); pool.Release()` — category: class-name

### Фоновые задачи (architecture / class-name)

- **DOCS-CEA-015** — [Docs/CommandExecutionAlgorithm.md:272-297] `BACKGROUND CLEANUP: ReleaseExpiredLeasesAsync() — UPDATE Commands SET Status='pending', Lease=NULL, ... WHERE Status='processing' AND Lease < @Now. Используется pg_try_advisory_lock(1234567) для предотвращения дублирования. ReleaseTimeoutCommandsAsync() — UPDATE Commands SET Status='pending', ... WHERE Status='processing' AND StartedAt < NOW() - INTERVAL` — category: class-name

### Отмена команды (class-name / architecture)

- **DOCS-CEA-016** — [Docs/CommandExecutionAlgorithm.md:303-327] `User: /status → кнопка "⛔ Отменить" → Server: DeleteCommandAsync() → DB: UPDATE Commands SET Status='Deleted' WHERE CommandId=@Id. Если процесс уже выполняется, он завершится штатно. UpdateStatus не перезаписывает Deleted` — category: class-name

### Жизненный цикл статусов (architecture)

- **DOCS-CEA-017** — [Docs/CommandExecutionAlgorithm.md:333-361] `pending (Создана INSERT в БД) → processing (Захвачена Worker-ом) → Done | Failed | Deleted. Failed → (retry) → pending. Done → NOTIFY command_completed → Server → Telegram-уведомление` — category: architecture

### Priority-based партиции (схема) (architecture)

- **DOCS-CEA-018** — [Docs/CommandExecutionAlgorithm.md:368-386] `Очередь команд (Order by Priority ASC, CreatedAt ASC, CommandId ASC). Маршрутизация по порогам: P <= 1 → Critical → SemaphoreSlim(3); P <= 2 → High → SemaphoreSlim(5); P <= 3 → Medium → SemaphoreSlim(3); P <= 4 → Low → SemaphoreSlim(1); P <= 5 → Lowest → SemaphoreSlim(1)` — category: architecture

### Lease-механизм (architecture)

- **DOCS-CEA-019** — [Docs/CommandExecutionAlgorithm.md:392-407] `Worker захватывает команду: Lease = ProcessTimeoutSeconds + 5 мин (в секундах Unix). При крахе Worker-а другой Worker через 5 минут: UPDATE Commands SET Status='pending', Lease=NULL, ErrorMessage='Lease expired: ...' WHERE Status='processing' AND Lease < @CurrentTimeSec` — category: architecture

---

## Docs/qodana-setup.md (68 строк)

### Обзор Qodana (other / dependency)

- **QODANA-001** — [Docs/qodana-setup.md:3] `Qodana — это платформа для контроля качества кода от JetBrains, которая переносит проверки из Rider/ReSharper в CI/CD.` — category: other
- **QODANA-002** — [Docs/qodana-setup.md:15] `docker run --rm -v ${PWD}:/data/project/ -p 8080:8080 jetbrains/qodana-dotnet --show-report` — category: other
- **QODANA-003** — [Docs/qodana-setup.md:17] `После завершения отчет будет доступен по адресу `http://localhost:8080`.` — category: other
- **QODANA-004** — [Docs/qodana-setup.md:21] `Создайте файл `.github/workflows/qodana.yml`:` — category: other
- **QODANA-005** — [Docs/qodana-setup.md:24] `name: Qodana` — category: other
- **QODANA-006** — [Docs/qodana-setup.md:30-31] `on: workflow_dispatch: pull_request: push: branches: [main, master]` — category: other
- **QODANA-007** — [Docs/qodana-setup.md:35] `runs-on: ubuntu-latest` — category: other
- **QODANA-008** — [Docs/qodana-setup.md:37-39] `permissions: contents: write, pull-requests: write, checks: write` — category: other
- **QODANA-009** — [Docs/qodana-setup.md:41] `- uses: actions/checkout@v4` — category: dependency
- **QODANA-010** — [Docs/qodana-setup.md:46] `- name: 'Qodana Scan' uses: JetBrains/qodana-action@v2024.1` — category: dependency
- **QODANA-011** — [Docs/qodana-setup.md:48] `env: QODANA_TOKEN: ${{ secrets.QODANA_TOKEN }}` — category: other
- **QODANA-012** — [Docs/qodana-setup.md:52] `Файл `qodana.yaml` в корне проекта содержит основные настройки` — category: other
- **QODANA-013** — [Docs/qodana-setup.md:53] `linter: используемый образ линтера (`jetbrains/qodana-dotnet`)` — category: dependency
- **QODANA-014** — [Docs/qodana-setup.md:54] `dotnet: путь к решению (`TelegramBot.slnx`)` — category: dependency
- **QODANA-015** — [Docs/qodana-setup.md:55] `profile: используемый профиль проверок (`qodana.recommended`)` — category: other
- **QODANA-016** — [Docs/qodana-setup.md:59] `Текущий CI-пайплайн (`.github/workflows/ci.yml`) включает:` — category: other
- **QODANA-017** — [Docs/qodana-setup.md:60-62] `dotnet format --verify-no-changes` — проверка стиля кода; `dotnet build` — проверка сборки; `dotnet publish` — публикация артефакта` — category: build-cmd
- **QODANA-018** — [Docs/qodana-setup.md:64] `Qodana пока не интегрирована в CI. Для добавления используйте workflow из раздела «Настройка в CI/CD».` — category: other
- **QODANA-019** — [Docs/qodana-setup.md:67] `Тесты в данном проекте отключены согласно AGENTS.md. Qodana настроена только на анализ статического кода.` — category: other
- **QODANA-020** — [Docs/qodana-setup.md:68] `Используется .NET 10. Убедитесь, что используемая версия линтера поддерживает этот SDK.` — category: dependency

---

## .github/copilot-instructions.md (5 строк)

- **COPILOT-001** — [.github/copilot-instructions.md:4] `Все реализуемые методы Telegram API должны быть актуальными и не устаревшими (без deprecated-подходов).` — category: other
- **COPILOT-002** — [.github/copilot-instructions.md:5] `Поддерживайте хорошую читаемость кода и унифицируйте методы для упрощения редактирования.` — category: other

---

## ДУБЛИ И ПРОТИВОРЕЧИЯ МЕЖДУ ДОКУМЕНТАМИ

### 1. Дубли файлов в `Docs/`

- **`Docs/execution-algorithm.md`** (1431 строка, 90 КБ) и **`Docs/CommandExecutionAlgorithm.md`** (407 строк, 26 КБ) — оба файла описывают один и тот же алгоритм выполнения команд.
  - `Docs/CommandExecutionAlgorithm.md:4` явно ссылается: `Полная спецификация: execution-algorithm.md`
  - `Docs/execution-algorithm.md` является полной (и более новой) версией; `CommandExecutionAlgorithm.md` — сокращённый sequence-diagram в текстовом виде (соответствует `CommandExecutionAlgorithm.puml`).
  - **Это явный дубликат по содержанию** — оба файла покрывают: создание задачи, Worker просыпается, захват команд, priority-based партиции, валидация FilePath, запуск процесса, завершение процесса, уведомление пользователя, cleanup, фоновые задачи, отмена команды, жизненный цикл статусов, lease-механизм.

### 2. Противоречия и расхождения между документами

| ID | Утверждение | Где | Противоречие / расхождение |
|---|---|---|---|
| **DUP-001** | Polling интервал: 1 мин vs 5 мин | `ROADMAP.md:31` (Polling очереди команд раз в 1 минуту) vs `AGENTS.md:179`, `Docs/execution-algorithm.md:33/285/303/626/821/967/1158/1389` (LISTEN new_tasks + fallback polling 5 мин) | ROADMAP говорит про 1 мин, все остальные — про 5 мин. ROADMAP устарел (v1.0) и не учитывает добавление LISTEN/NOTIFY в v1.1. |
| **DUP-002** | Lease TTL | `Docs/execution-algorithm.md:46,994` (5 мин Lease) vs `ROADMAP.md:33,53` (`Lease = ProcessTimeoutSeconds + 5 мин`, долгий TTL) vs `Docs/execution-algorithm.md:454-455,1402` (`Lease устанавливается на ProcessTimeoutSeconds + 5 мин`) | `execution-algorithm.md` в одном месте говорит "если через 5 минут" (как в v1.0), в другом — `ProcessTimeoutSeconds + 5 мин` (v1.1). Дрейф в самом execution-algorithm.md. |
| **DUP-003** | ProcessTimeout default | `Docs/execution-algorithm.md:46` (3600 сек = 1 час) vs `Docs/execution-algorithm.md:465` (`// 3600 сек = 1 час`) vs `Docs/execution-algorithm.md:815,832,1385` (10800 сек = 3 часа, default в v1.1+) | В одном и том же файле — расхождение: "1 час" в комментариях к коду vs "3 часа" в таблицах конфигурации. |
| **DUP-004** | Status lifecycle — промежуточный статус отмены | `AGENTS.md:215` («⛔ Отменить» использует тот же soft-delete `Status = 'Deleted'`) vs `Docs/execution-algorithm.md:400,650-652` (отдельно отмечает, что "отдельного промежуточного статуса отмены, per-command CTS и отдельного cancel-уведомления нет") | Согласованы — это история, а не противоречие. Но AGENTS.md явно, а execution-algorithm.md развёрнуто. |
| **DUP-005** | CleanupIntervalSec | `Docs/execution-algorithm.md:269,455,819` (5 мин) vs `ROADMAP.md:54` (60 сек) | Расхождение: 5 мин vs 60 сек. Возможно ROADMAP устарел. |
| **DUP-006** | Количество callback-хендлеров | `ROADMAP.md:21` (7 хендлеров) vs `AGENTS.md:209` (AccessRequestHandler, FileNavigationHandler, FileSelectionHandler, CommandToggleHandler, SessionManagementHandler, CommandSelectionHandler = 6 хендлеров) | 7 vs 6 — расхождение. Возможно в ROADMAP устаревшее значение. |
| **DUP-007** | Worker | `AGENTS.md:20-21` vs `README.md:42-50` vs `ROADMAP.md:11` — все сходятся: 4 проекта Core/Data/Server/Worker + BimLib внутри Worker. | Согласованы. |
| **DUP-008** | Воркеры и NOTIFY | `AGENTS.md:179` (LISTEN/NOTIFY new_tasks + fallback 5 мин) vs `ROADMAP.md:147-148` (new_command LISTEN/NOTIFY не требуется, polling раз в минуту) | AGENTS говорит new_tasks NOTIFY активен, ROADMAP в "не планируется" говорит, что new_command NOTIFY не нужен. Это разные каналы — возможно, не противоречие, но разные формулировки сбивают с толку. |
| **DUP-009** | Длительность lease | `Docs/execution-algorithm.md:46` — Lease 5 мин, в примере кода; `Docs/execution-algorithm.md:454-455,1402` — Lease = ProcessTimeoutSeconds + 5 мин | Расхождение внутри одного файла. |
| **DUP-010** | Порядок batch size | `Docs/execution-algorithm.md:308` (DefaultBatchSize=5) vs `Docs/CommandExecutionAlgorithm.md:70` (DefaultBatchSize=5) vs `Docs/CommandExecutionAlgorithm.md:80` (LIMIT 50) | CEA внутри себя противоречит: в одном месте limit=5, в SQL limit=50. Это похоже на опечатку. |
| **DUP-011** | App Service — что входит в /status | `AGENTS.md:215` (SESSIONDETAILS/DELETESESSION/DELETECOMMAND/CONFIRMDELETESESSION/CONFIRMDELETECOMMAND) vs `ROADMAP.md:118-119` (CONFIRMDELETESESSION/CONFIRMDELETECOMMAND) vs `ROADMAP.md:227` (DELETESESSION/DELETECOMMAND) | Согласованы по сути (подтверждение + удаление), но перечисления разной полноты. |
| **DUP-012** | Дата актуальности ROADMAP | `ROADMAP.md:3` (`Актуально на: 8 июня 2026`) — файл декларирует дату 8 июня 2026, тогда как git log показывает более поздние правки (8 июня 2026 / 9 июня 2026). | Возможно, дата в шапке не обновлялась. |

### 3. Утверждения, присутствующие только в одном из документов

| Утверждение | Только в | Нет в |
|---|---|---|
| `Telegram.Bot 22.10.0.1` | `README.md:27` | AGENTS.md, ROADMAP.md, Docs/* |
| `PostgreSQL 15+` | `README.md:37` | AGENTS.md, ROADMAP.md, Docs/* |
| `Docker` (рекомендуется) | `README.md:38, 158-167` | AGENTS.md, ROADMAP.md, Docs/* |
| `Telegram-бот для навигации...` (обзор) | `README.md:3` | Другие файлы |
| Команды бота `/start`, `/help`, `/export`, `/automation`, `/status` | `README.md:97-103` (таблица), `ROADMAP.md:20` (список) | AGENTS.md, Docs/* |
| `dotnet format TelegramBot.slnx` | `AGENTS.md:43`, `ROADMAP.md:201` (через `--diagnostics IDE0005`), `QODANA-017` (через `dotnet format --verify-no-changes`) | Согласованы (но разные флаги). |
| `dotnet test` запрещён | `AGENTS.md:46` | QODANA-019 (упомянуто "Тесты отключены согласно AGENTS.md") |
| Qodana настройка (workflow, image, profile) | `Docs/qodana-setup.md` | AGENTS.md, README.md, ROADMAP.md |
| `pg_try_advisory_lock(1234567)` | `ROADMAP.md:78`, `Docs/execution-algorithm.md:1408`, `Docs/CommandExecutionAlgorithm.md:286` | Согласованы |
| Команды `XLSEXPORT` как пример расширения | `Docs/execution-algorithm.md:1233-1238` | Другие файлы |
| GitNexus indexer | `AGENTS.md:370-401` | Другие файлы |
| Copilot instructions (просто "deprecated-подходы не использовать") | `.github/copilot-instructions.md` | Другие файлы |
| `2026-06-08` (actual date) | `ROADMAP.md:3` | Другие файлы |
| `dotnet-audit-2026` reference | — | (отсутствует во всех) |
| `Рекомендации по улучшению` (таблица с приоритетами 🔥🟠🟡) | `ROADMAP.md:219-235` | Другие файлы |

### 4. Уникальные утверждения AGENTS.md (не упомянутые явно в README/ROADMAP/Docs)

- Полный список 6 callback-хендлеров с приоритетами (AccessRequestHandler:0, FileNavigationHandler:10, FileSelectionHandler:20, CommandToggleHandler:100, SessionManagementHandler:100, CommandSelectionHandler:100)
- `CallbackDataParser.Parse(data)` + `ParsedCallback.Is(...)` pattern
- `MarkdownHelper.EscapeMarkdownV2()` vs `MarkdownHelper.EscapeMarkdown()`
- Удалённые интерфейсы (`IRevitPathResolver`, `IRevitProcessTracker`, `INavisworksProcessTracker`, `IFileSystemBrowser`, `ITelegramUpdateMapper`)
- Конкретные namespaces всех проектов
- Правила именования (PascalCase, `_camelCase`, `I`-prefix)
- Структура файла констант: `TelegramBot.Core/Constants/`
- Primary constructors — правила и примеры
- Telegram parse mode — MarkdownV2 для plain / Markdown для inline keyboards
- PostgreSQL типы данных: TIMESTAMPTZ, SERIAL, BIGINT
- SQL: `RETURNING`, `ON CONFLICT DO NOTHING/UPDATE`
- `Process` хранится напрямую, без обёртки (атомарный `AddOrUpdate` в `_sessionRemaining`)
- GitNexus workflow

### 5. Уникальные утверждения Docs/execution-algorithm.md (не упомянутые в AGENTS/README/ROADMAP)

- Паттерн "Strategy" — конфигурация команды определяет стратегию запуска (Revit, Navisworks, Python)
- `BimIntegration` appsettings keys: `MinSupportedVersion`, `MaxSupportedVersion`, `RevitInstallRoot`
- Семантика `Path.GetFileName()` для извлечения имён файлов в уведомлениях
- PowerShell команды для диагностики процессов Revit
- Полные SQL-блоки (INSERT, UPDATE с `unnest`, NOTIFY payload)
- Тех. долг 11 пунктов (DOC-001 — DOC-011)
- Диагностические SQL-запросы (8 штук)
- `Path.GetFullPath()` для защиты от path traversal

### 6. Уникальные утверждения ROADMAP.md (не упомянутые в других документах)

- Конкретные версии v1.0, v1.1, v1.2, v1.3, v2.0+ со статусами
- 8+ предложений по улучшению (pre-warm Revit, статистика, умный retry)
- 6 открытых вопросов
- "Не планируется" секция (Health checks, new_command LISTEN/NOTIFY)
- `~23 redundant поля` — конкретное число удалённых полей в C# 12 рефакторе
- `~15 ручных new NpgsqlConnection + OpenAsync` — конкретное число в CreateConnectionAsync
- `MaxRetries=5` (конкретное число)
- Удалённые SQL/интерфейсы в v1.2 (`GetCommandStatusAsync`, `GetFailedFilesBySession`, `IFileSystemBrowser`, `ITelegramUpdateMapper`, `IRevitPathResolver`, `IRevitProcessTracker`, `INavisworksProcessTracker`)
- Конкретный retry bug: retry раньше не декрементит `_sessionRemaining` (исправлен в v1.2)
- Конкретные значения конфига: `RateLimit:MaxFilesPerUserPerDay = 100`

### 7. Уникальные утверждения Docs/CommandExecutionAlgorithm.md (не упомянутые в других)

- `App = CommandAppService + SlashCommandService` — ServiceApp как комбинация
- `CollectRvtFiles()` — отдельный метод (не описан в AGENTS/README/ROADMAP/execution-algorithm)
- Конкретный SQL: `LIMIT 50` в `FOR UPDATE SKIP LOCKED` (расхождение с `DefaultBatchSize=5` в `execution-algorithm.md`)

### 8. Уникальные утверждения Docs/qodana-setup.md (не упомянутые в других)

- Workflow file: `.github/workflows/qodana.yml` (ещё не существует — файл не интегрирован в CI)
- Qodana image: `jetbrains/qodana-dotnet`
- Qodana action: `JetBrains/qodana-action@v2024.1`
- Qodana profile: `qodana.recommended`
- `dotnet format --verify-no-changes`

### 9. Уникальные утверждения .github/copilot-instructions.md

- Минимальный, всего 2 пункта. Не дублирует ничего — это руководство для Copilot-а.

---

## Итог

Готово. Файл: audit/doc-claims.md, 379 утверждений из 7 файлов.
