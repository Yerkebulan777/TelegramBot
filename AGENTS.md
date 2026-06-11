# AGENTS.md

Guidance for agentic coding agents working in this repository.

## Документация проекта

| Документ | Описание |
|----------|----------|
| [README.md](README.md) | Обзор проекта, запуск, конфигурация, команды бота |
| [ROADMAP.md](ROADMAP.md) | Дорожная карта, статус версий, план дальнейших работ |
| [Docs/ExecutionAlgorithm.md](Docs/ExecutionAlgorithm.md) | Спецификация алгоритма выполнения команд, SQL-запросы |
| **AGENTS.md** (текущий файл) | Архитектура, BimLib, DI, code style, константы для AI-агентов |

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
- **TelegramBot.Data** — PostgreSQL persistence via Dapper + Npgsql. References Core only. SQL constants in `Sql/` (5 partial files).
- **TelegramBot.Server** — Telegram infrastructure, application services, handlers, hosting, helpers. References Core + Data.
- **TelegramBot.Worker** — Background service for executing Revit/Navisworks/AI tasks. Polls PostgreSQL for pending commands. References Core + Data. BimLib is embedded inside this project as `Worker/BimLib/` (not a separate project).



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

> **Roadmap:** См. [ROADMAP.md](ROADMAP.md). **Обзор:** [README.md](README.md). **Алгоритм:** [Docs/ExecutionAlgorithm.md](Docs/ExecutionAlgorithm.md).

---

## Configuration

Подробная конфигурация с примерами — в [README.md](README.md#конфигурация).

- `TelegramBot.Server/appsettings.json` — committed, Serilog, `FileSystem`, `ConnectionStrings:Postgres`
- `TelegramBot.Server/appsettings.Local.json` — **gitignored**, secrets (bot token)
- `TelegramBot.Worker/appsettings.json` — committed, `ConnectionStrings:Postgres`
- Required keys: `TelegramBot:Token`, `TelegramBot:AdminUserIds`, `FileSystem:RootPath`, `ConnectionStrings:Postgres`, `RateLimit:MaxFilesPerUserPerDay`, `Worker:CompletedSessionRetentionDays`
- **HealthCheck** section: `Port` (5000 Server / 5001 Worker), `ServiceName`, `CacheSeconds`, `DbCheckTimeoutSeconds`

---

## Architecture & Request Flow

```
Telegram API -> TelegramBotHostedService (polling)
             -> TelegramUpdateMapper (Update -> MessageDto | CallbackQueryDto)
             -> CommandAppService.HandleUserCommandAsync (text commands)
                ├── /start bypasses access check → registration or help
                └── other commands → BotUsers.Status must be Approved
             -> CallbackDispatcher.DispatchAsync (inline keyboard callbacks)
                ├── REQACCESS/APPROVEUSER/REJECTUSER bypass access check
                └── all other callbacks → user must be Approved
```

### BimLib (BIM Integration) — embedded in Worker

BimLib is a **Windows-only** set of modules located inside the Worker project (`TelegramBot.Worker/BimLib/`). It provides BIM-related infrastructure used by `CommandExecutionService`.

**Structure:**

| Folder | Contents |
|--------|----------|
| `Config/` | `BimIntegrationOptions` — min/max supported Revit version, install root path |
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
services.AddSingleton<RevitVersionDetector>();
services.AddSingleton<RevitPathResolver>();
services.AddSingleton<RevitProcessTracker>();
services.AddSingleton<DialogDismisser>();
services.AddSingleton<NavisworksPathResolver>();
services.AddSingleton<NavisworksProcessTracker>();
```
Requires `BimIntegrationOptions` config section in Worker's `appsettings.json`.

**Namespaces:**
- `TelegramBot.BimLib.Config`
- `TelegramBot.BimLib.Models`
- `TelegramBot.BimLib.Monitor`
- `TelegramBot.BimLib.Native`
- `TelegramBot.BimLib.Services`

### Worker Components

`CommandExecutionService` is a slim orchestrator delegating to:

| Component | Role |
|-----------|------|
| `PartitionPoolManager` | Per-priority `SortedDictionary<int, SemaphoreSlim>` pools. `IDisposable`. |
| `CommandPreparer` | Validates FilePath, resolves BIM executables via BimLib, creates `ProcessStartInfo`. Clones `CommandConfig` before mutating (never mutates shared `IOptions` objects). |
| `ProcessRunner` | `RunAsync(cmd, ct)`: execute → timeout/retry → dispose. Owns `_activeProcesses`. Uses unique `attemptToken` (GUID) per execution attempt for temp-file isolation. Cleans up temp files in `finally`. |
| `SessionCompletionTracker` | In-memory `ConcurrentDictionary<int,int>` counter + DB confirmation for notifications |
| `SessionCleanupService` | `BackgroundService`: soft-deletes inactive sessions older than `CompletedSessionRetentionDays`. `0` disables. |

**Key behaviour changes (v1.7):**
- **`CommandExecutionService`** uses a **drain loop** (`DrainPendingCommandsAsync`) instead of blocking `Task.WhenAll`. Commands are fired as background tasks and immediately tries to claim more, eliminating head-of-line blocking. Tracks running tasks via `_runningTasks` (HashSet with lock) for shutdown.
- **`CommandPreparer.PrepareAsync`** creates a **clone of `CommandConfig`** before setting `ExecutablePath`, avoiding data races on the shared `IOptions` singleton.
- **`CommandPreparer.CreateTaskFile`** uses atomic write (`.tmp` + `File.Move`) and accepts an `attemptToken` for unique temp-file names per attempt.
- **`ProcessRunner`** generates a unique `attemptToken` (Guid) per attempt. All temp files include this nonce: `task_{CommandId}_{nonce}.json`, `result_{CommandId}_{nonce}.json`. Temp files are cleaned up in `finally` via `CommandPreparer.CleanupTempFiles`.

**DI registration** — в `Worker/Program.cs`:
```csharp
services.AddSingleton<PartitionPoolManager>();
services.AddSingleton<CommandPreparer>();
services.AddSingleton<ProcessRunner>();
services.AddSingleton<SessionCompletionTracker>();
services.AddHostedService<CommandExecutionService>();
services.AddHostedService<SessionCleanupService>();
```



---

**Important notes for AI agents:**
- BimLib is `[SupportedOSPlatform("windows")]` — Windows only (Registry + P/Invoke). OpenMcdf 3.x parses .rvt OLE streams.
- `RevitProcessStatus` enum: `Healthy`, `NotResponding`, `Error`.
- Removed BimLib interfaces: `IRevitPathResolver`, `IRevitProcessTracker`, `INavisworksProcessTracker`, `IRevitVersionDetector`, `INavisworksPathResolver` (concrete classes only).

### How Revit Commands Actually Work

**The hard truth:** Revit.exe is a GUI application, not a console tool. It does not write to stdout/stderr and does not understand `/command` arguments out of the box. To execute PDF/DWG/IFC/BIMDOC commands, a **custom Revit AddIn (plugin)** must be installed on the server.

#### Plugin API — JSON File Exchange (TaskFile + ResultFile)

Worker и плагин обмениваются данными через JSON-файлы во временной папке:

| Файл | Кто создаёт | Кто читает | Назначение |
|------|------------|------------|------------|
| `task_{CommandId}_{AttemptToken}.json` | Worker (`CommandPreparer.CreateTaskFile`) | Плагин | Задание: что и с каким файлом делать |
| `result_{CommandId}_{AttemptToken}.json` | Плагин | Worker (`ProcessRunner.TryReadResultFile`) | Результат: успех/ошибка, выходные файлы |

**TaskFile** (`TelegramBot.Core.Models.TaskFile`):
```json
{
  "commandId": 42,
  "commandText": "PDF",
  "filePath": "B:\\project.rvt",
  "resultFilePath": "C:\\Temp\\result_42_abc123.json",
  "options": {}
}
```
- `commandId` — ID команды в БД
- `commandText` — тип экспорта (PDF, DWG, IFC, BIMDOC, NWC, CLASHREP, AUTORES)
- `filePath` — полный путь к исходному файлу
- `resultFilePath` — путь, куда плагин должен записать результат
- `options` — дополнительные опции (расширяемый словарь)

**ResultFile** (`TelegramBot.Core.Models.ResultFile`):
```json
{
  "status": "done",
  "errorMessage": null,
  "outputFiles": ["B:\\output.pdf"]
}
```
- `status` — `"done"` или `"failed"` (обязательное поле)
- `errorMessage` — сообщение об ошибке (опционально, при status = "failed")
- `outputFiles` — список сгенерированных файлов (опционально, при status = "done")

Алгоритм работы:
1. Worker создаёт `task_{CommandId}_{AttemptToken}.json` в `Path.GetTempPath()` (атомарная запись: `.tmp` → `File.Move`)
2. Worker запускает Revit.exe/Navisworks.exe/python с аргументами командной строки
   (плагин получает путь к task-файлу как аргумент `{TaskFilePath}`)
3. Плагин читает `task_{CommandId}_{AttemptToken}.json`, выполняет экспорт
4. Плагин пишет `result_{CommandId}_{AttemptToken}.json` по указанному `resultFilePath` (рекомендуется: `.tmp` → `File.Move` для атомарности)
5. Worker читает `result_{CommandId}_{AttemptToken}.json`:
   - Сначала парсит JSON, потом удаляет файл (при битом JSON → переименовывает в `.bad` для диагностики)
   - Обновляет статус команды в БД
6. Temp-файлы очищаются в `finally` блока `ProcessRunner.RunAsync()`

**Плагин должен:
- Прочитать task-файл при запуске
- Выполнить команду (PDF, DWG, IFC и т.д.)
- Записать result-файл в указанный путь
- Завершить процесс с exit code 0, если result-файл успешно записан**

Если result-файл не найден — Worker использует fallback по exit code процесса
(0 = Done, иначе Failed с retry или без).

#### Command-line arguments

Плагин получает аргументы командной строки (шаблон `ArgumentsTemplate` в `appsettings.json`):

```
Revit.exe /command "PDF" "B:\project.rvt" "C:\Temp\task_42_abc123.json"
```

Доступные плейсхолдеры в шаблоне:
| Плейсхолдер | Описание |
|-------------|----------|
| `{CommandText}` | Тип экспорта (PDF, DWG, IFC...) |
| `{FilePath}` | Полный путь к исходному файлу |
| `{CommandId}` | ID команды в БД |
| `{TaskFilePath}` | Полный путь к `task_{CommandId}.json` |
| `{ResultFilePath}` | Полный путь к `result_{CommandId}.json` |

**Рекомендуемый подход:** плагин должен читать task-файл, а не полагаться только
на аргументы командной строки — JSON содержит полную структурированную информацию.

> **Важно (v1.7):** Имена temp-файлов включают уникальный `AttemptToken` (GUID без дефисов)
> для каждой попытки выполнения. Это предотвращает: (1) подсовывание ложного result локальным
> процессом (predictable filenames), (2) чтение stale result от предыдущей retry-попытки,
> (3) конфликты между параллельными выполнениями одной команды.

#### Without a plugin (broken flow):

```
Worker → Revit.exe opens as GUI
         ↓
         Revit just sits there, showing an empty project
         ↓
         3 hours later → Worker kills it → Command timed out → Failed
```

The `DialogDismisser` monitors the process and auto-closes modal dialogs (error popups, warnings), but if no plugin is installed, Revit doesn't know what to do with `/command` and just opens normally, ignoring the arguments.

#### Other command types:

| Type | Executable | stdout/stderr | Result mechanism |
|------|-----------|---------------|------------------|
| PDF, DWG, IFC, BIMDOC | `Revit.exe` | **No** — GUI app | TaskFile + ResultFile JSON exchange |
| NWC, CLASHREP | `FileConvert.exe` | **Yes** — console utility | TaskFile + ResultFile, fallback to exit code |
| AUTORES | `python ai_agent.py` | **Yes** — console script | TaskFile + ResultFile via `--task` `--result`, fallback to exit code |

**Shutdown behavior:** When Worker shuts down, it **kills all active processes** (`Kill(entireProcessTree: true)`) and waits up to 10 seconds for them to die. No orphaned Revit processes remain on the server. (The 3-hour timeout handle in `HandleTimeoutAsync` also uses `Kill(true)` — processes are always killed, not left running.)

**Temp-file cleanup (v1.7):** Temp files (`task_*.json`, `result_*.json`) are cleaned up per-attempt in the `finally` block of `ProcessRunner.RunAsync()`. Each attempt uses a unique `attemptToken` (GUID), preventing stale-file conflicts between retries.

### Shared Static Helpers

| Helper | Location | Purpose |
|--------|----------|---------|
| `HandlerHelpers` | `Server/Handlers/HandlerHelpers.cs` | `SendActionsReplyKeyboardAsync()` — reply-клавиатура + трекинг |
| `ProcessHealthHelper` | `BimLib/Monitor/ProcessHealthHelper.cs` | `CheckHealth()` — общая для Revit и Navisworks |
| `NpgsqlHelper` | `TelegramBot.Data/NpgsqlHelper.cs` | Единый helper подключения |
| `BimLibLogFilter` | `Worker/Services/BimLibLogFilter.cs` | Фильтр логов для `TelegramBot.BimLib.*` |
| `PostgresReconnectLoop` | `TelegramBot.Data/PostgresReconnectLoop.cs` | Outer retry loop для переподключения PostgreSQL (5 сек) |
| `ErrorClassifier` | `TelegramBot.Worker/Services/ErrorClassifier.cs` | Классификация ошибок: InvalidFileError → сразу Failed, ProcessCrashError → retry |
| `CommandPreparer.CleanupTempFiles` | `TelegramBot.Worker/Services/CommandPreparer.cs` | Очистка temp-файлов task/result для указанной попытки |

---

## Constants Reference

All constants are located in `TelegramBot.Core/Constants/`. Use these instead of hardcoded strings/ints.

| File | Purpose | Key Constants |
|------|---------|---------------|
| `CallbackPrefixes.cs` | Inline keyboard callback prefixes | `GoToParent`, `File`, `Pdf`, `SessionDetails`, `DeleteSession`, `RequestAccess`, etc. |
| `CommandCodes.cs` | Export command identifiers | `Pdf`, `Dwg`, `Nwc`, `Ifc`, `BimDoc`, `ClashRep`, `AutoRes` |
| `Statuses.cs` | Entity statuses (commands/sessions) | `Pending`, `Processing`, `Done`, `Failed`, `Deleted`, `FinalStatuses`, `ActiveStatuses` |
| `CommandPriorities.cs` | Worker queue priority levels | `Critical`, `High`, `Medium`, `Low`, `Default` |
| `ButtonTexts.cs` | Reply keyboard button labels | `Apply`, `Confirm`, `Cancel` |

**Important notes:**
- All callback prefixes end with `:` (colon) for data concatenation
- `Statuses.FinalStatuses` includes `Done`, `Failed`, `Deleted` — used to check if an entity is terminal
- `Statuses.ActiveStatuses` includes `Pending`, `Processing` — used to find uncompleted entities
- Command codes match callback prefix names (e.g., `CommandCodes.Pdf` = `"PDF"`, `CallbackPrefixes.Pdf` = `"PDF:"`)
- Button texts include emoji and are used with reply keyboards (not inline keyboards)

### Task Execution Flow (Server → PostgreSQL → Worker)

Полная спецификация алгоритма: **[Docs/ExecutionAlgorithm.md](Docs/ExecutionAlgorithm.md)**

```
SlashCommandService.ConfirmFileSelectionAsync()
    │
    ├── dataService.CreateSessionWithCommandsAsync() -- INSERT INTO Commands (ProjectName)
    │
    ▼
    CommandExecutionService (Worker)
        LISTEN/NOTIFY new_tasks (мгновенная реакция) + fallback polling (5 мин)
        dataService.ClaimPendingCommandsAsync() -- FOR UPDATE SKIP LOCKED
        ExecuteOneAsync(cmd) -- запуск Revit/Navisworks/AI
        dataService.UpdateCommandStatusAsync() -- UPDATE Status='Done'/'Failed'
        CompleteClaimedCommandAsync() -- декремент batch-счётчика + проверка финальности по БД
            └── dataService.NotifySessionCompletedAsync() -- NOTIFY command_completed
                                                              │
                   ┌──────────────────────────────────────────┘
                   ▼
    CommandNotificationService (Server)
        Получает NOTIFY → парсит payload (SessionId|CorrelationId)
        → NotificationSenderService вызывает SessionDataService.GetSessionCompletionSummaryAsync()
        → telegramOutput.SendMessageAsync() со сводкой по сессии
```

**In-memory счётчик сессий:** вместо per-command SQL запроса `GetSessionProgressAsync`
Worker использует `ConcurrentDictionary<int, int> _sessionRemaining` как batch-local оптимизацию.
При `ClaimPendingCommandsAsync` счётчик заполняется по `GroupBy(SessionId)`,
при каждом выходе захваченной команды из `processing` (Done/Failed/retry) атомарно декрементится через `AddOrUpdate`.
Уведомление отправляется только когда `remaining == 0` и БД подтверждает, что в сессии больше нет `pending`/`processing`.
Payload `command_completed` минимальный: `SessionId|CorrelationId`. Сводка сообщения строится на Server через
`SessionDataService.GetSessionCompletionSummaryAsync()` и включает длительность сессии (`MIN(StartedAt)` → `MAX(CompletedAt)`)
и список ошибочных файлов.

DI is wired in `TelegramBot.Server/Extensions/DependencyInjectionExtensions.cs`. The filesystem root comes from `FileSystemOptions` (bound to `"FileSystem"` config section). The Worker registers data services (`UserDataService`, `CommandDataService`, `SessionDataService`, `MessageTrackingDataService`) directly in `Program.cs`.

**Key DI simplification:** All single-implementation interfaces have been removed — consumers now depend on concrete types directly:
- `IFileSystemBrowser` → `FileSystemBrowser`
- `ITelegramUpdateMapper` → `TelegramUpdateMapper`
- `ICallbackDispatcher` → `CallbackDispatcher`
- `ICommandAppService` → `CommandAppService`
- `IDatabaseInitializer` → `DatabaseInitializerService`
- `ISlashCommandService` → `SlashCommandService`
- `IAccessValidator` → `AuthorizationMiddleware`
- `ISessionManager` → `SessionManager`
- `IKeyboardBuilder` → `KeyboardBuilder`
- `IRevitVersionDetector` → `RevitVersionDetector`
- `INavisworksPathResolver` → `NavisworksPathResolver`

### Health Check Endpoints

Оба приложения (Server и Worker) запускают `HealthCheckHostedService` — минимальный HTTP-сервер на `TcpListener`:

| Endpoint | Описание |
|----------|----------|
| `GET /health/live` | Liveness — процесс жив (всегда 200) |
| `GET /health/ready` | Readiness — проверка PostgreSQL (200 или 503) |
| `GET /health` | Подробный JSON: статус, checks (database, process), uptime, версия |

**Конфигурация** (секция `HealthCheck` в appsettings.json):

```json
"HealthCheck": {
  "Port": 5000,
  "ServiceName": "TelegramBot.Server",
  "CacheSeconds": 10,
  "DbCheckTimeoutSeconds": 5
}
```

**Server** — порт 5000, регистрация в `DependencyInjectionExtensions.AddHealthCheckServices()`.
**Worker** — порт 5001, регистрация в `Program.cs`.

**Namespace:** `TelegramBot.Core.Health` (`HealthCheckHostedService`, `HealthCheckResult`, `HealthComponent`).
**Config:** `TelegramBot.Core.Config.HealthCheckOptions` (секция `"HealthCheck"`).

Результат `/health` кэшируется на `CacheSeconds` секунд. Readiness проверяет PostgreSQL через `NpgsqlHelper`.
Поддерживает расширение через `AdditionalChecks` dict для добавления компонентов в отчёт.

**Worker-specific checks** (регистрируются в `Program.cs`):

| Проверка (`AdditionalChecks` key) | Описание | Статус unhealthy |
|-----------------------------------|----------|------------------|
| `bimInstallRoot` | Проверяет существование `BimIntegration.RevitInstallRoot` | Директория не найдена |
| `activeProcesses` | Количество активных внешних процессов | Всегда `healthy` (информационно) |

### Smart Retry — ErrorClassifier

`ProcessRunner.HandleFailureAsync` использует `ErrorClassifier` для классификации ошибок при выполнении команд:

| Тип | Поведение | Примеры |
|-----|-----------|--------|
| `InvalidFileError` (permanent) | сразу `Failed`, без retry | Файл не найден, нет доступа, неверный формат |
| `ProcessCrashError` (transient) | retry с экспоненциальной задержкой (`60s * 2^(attempt-1)`) | Процесс упал с общим кодом ошибки |

**Критерии permanent:**
1. **По тексту ошибки** — паттерны: `"not found"`, `"access denied"`, `"invalid file"`, `"permission denied"`, `"cannot open file"` и др.
2. **По exit code** — если входит в `WorkerOptions.PermanentFailureExitCodes` (настраивается в `appsettings.json`, по умолчанию пусто)
3. **По типу исключения** — `FileNotFoundException`, `DirectoryNotFoundException`, `UnauthorizedAccessException`, `PathTooLongException`

**Config:** `WorkerOptions.PermanentFailureExitCodes` (`HashSet<int>`, по умолчанию пустой).
**Namespace:** `TelegramBot.Worker.Services.ErrorClassifier`.

### Callback Handling — Chain of Responsibility

`CallbackDispatcher` routes callbacks to the first `ICallbackHandler` that `CanHandle()` the prefix (sorted by `Priority`, lower = first). All handlers extend `CallbackHandlerBase`.

Handler hierarchy: `AccessRequestHandler` (Priority 0) > `FileNavigationHandler` (10) > `FileSelectionHandler` (20) > `CommandToggleHandler`, `SessionManagementHandler`, `CommandSelectionHandler` (100).

**Error handling:** `CallbackHandlerBase.HandleAsync()` does NOT catch exceptions — they propagate to `CallbackDispatcher.DispatchAsync()`, which catches `Exception`, logs it, and continues to the next handler. This eliminates double logging.

Callback prefixes are constants in `CallbackPrefixes` (`TelegramBot.Core/Constants/CallbackPrefixes.cs`). Command codes in `TelegramBot.Core/Constants/CommandCodes.cs`. Use `CallbackDataParser.Parse(data)` (from `ParsedCallback.cs`) to get a `ParsedCallback`, then match with `parsed.Is(CallbackPrefixes.GoToParent)`. 

> **SessionManagementHandler** manages `/status` actions via `SESSIONDETAILS:`, `DELETESESSION:`, `DELETECOMMAND:`, `CONFIRMDELETESESSION:`, and `CONFIRMDELETECOMMAND:`. Delete buttons first show a confirmation dialog; the «⛔ Отменить» button for a running command uses the same soft-delete path as command deletion: `Status = 'Deleted'`.

> **Race condition fix (v1.7):** `SessionManager.GetOrCreateSession()` no longer calls `RemoveSession()` (which could `Dispose()` a `SemaphoreSlim` currently held by the calling thread via `AcquireUserLockAsync`). Session expiry now only removes the entry from `_sessions` without touching `_sessionLocks`. Background `CleanUpExpiredSessionsAsync` safely handles both `_sessions` and `_sessionLocks` disposal.

For Markdown escaping, use `MarkdownHelper.Escape()` from `TelegramBot.Server/Helpers/`.

### Database

Tables: `BotUsers`, `Sessions`, `Commands`, `TrackedMessages`. Message tracking is fully DB-backed — no in-memory state. Soft-delete only — set `Status = 'Deleted'`, never `DELETE FROM`. Worker auto-cleanup also uses soft-delete for inactive sessions older than `Worker:CompletedSessionRetentionDays`.

**`Commands` table includes `Partition` field** — partition threshold для priority-based пулов процессов.

**`Sessions` table now includes `ProjectName TEXT`** — имя проекта записывается при создании сессии,
отображается в `/status` и в уведомлениях о завершении.

**`GetCommandStatusAsync` removed** — was dead code. Deleted commands never appear as `'pending'`
in `ClaimPendingCommandsAsync`, so the separate cancellation check was redundant.

**`CountPendingProcessingBySessionAsync` added** — используется в `CompleteClaimedCommandAsync`
для проверки, не осталось ли ещё pending/processing команд в БД (корректно обрабатывает случай,
когда команд в сессии > DefaultBatchSize).

Database: **PostgreSQL** via Npgsql. Initialized at startup via `host.InitializeDatabaseAsync()` + `host.SeedAdminUsersAsync()`.
All data access uses **Dapper** (in `TelegramBot.Data/` — `CommandDataService.cs`, `SessionDataService.cs`, `UserDataService.cs`, `MessageTrackingDataService.cs`). Connection creation is unified via `CreateOpenConnectionAsync()` helper in `DataAccessBase` (replaces ~15 manual `new NpgsqlConnection + OpenAsync` patterns). A public static `NpgsqlHelper.CreateOpenConnectionAsync()` is also used by `CommandNotificationService` and `NotificationSenderService` on the Server side. SQL constants in `TelegramBot.Data/Sql/` (5 partial files total: `Queries.Schema.cs`, `Queries.Users.cs`, `Queries.Sessions.cs`, `Queries.Commands.cs`, `Queries.TrackedMessages.cs`).

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
- **Interface policy (v1.4):** Single-implementation interfaces have been removed project-wide.
  - **Keep** `ICallbackHandler` — Chain of Responsibility (6 implementations).
  - **All other interfaces removed** (19 interfaces).
  - `ITelegramOutputService` remains — sole consumer interface in `TelegramBot.Server.Interfaces`.
  - **Do not create** new single-implementation interfaces.

### Async / Await

- All async methods return `Task` or `Task<T>` — never `async void` (exception: Npgsql event handlers in `CommandNotificationService` — suppressed via `#pragma warning disable VSTHRD100`)
- Always suffix async methods with `Async` — enforced by `Microsoft.VisualStudio.Threading.Analyzers` (VSTHRD200, `WarningsAsErrors`)
- `VSTHRD003` (foreign Task), `VSTHRD103` (sync blocking) are also treated as errors — all violations fixed or suppressed with documented pragmas
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
- `RateLimiter` (Core/Services) uses `ConcurrentDictionary<long, RequestWindow>` with queue-based sliding window per-user
- Paths in callback data are passed directly (no `PathMap`/tokens) since v1.1 refactoring
- For new shared dictionaries, prefer `ConcurrentDictionary<,>`

### SQL / Data Access (TelegramBot.Data)

- Use `await using var conn = await CreateOpenConnectionAsync()` — connection creation is unified via `DataAccessBase.CreateOpenConnectionAsync()` (protected) or `NpgsqlHelper.CreateOpenConnectionAsync()` (static/public)
- Use **Dapper** for all queries (no raw `NpgsqlCommand`/`NpgsqlDataReader`)
- SQL statements go in verbatim string literals (`@"..."`)
- Use parameterized queries — never string-concatenate user input into SQL
- Soft-delete only: `SET Status = 'Deleted'`, never `DELETE FROM`
- For transactions, use `conn.BeginTransactionAsync()`
- Use `RETURNING` clause for INSERT to get generated IDs (not `last_insert_rowid()`)
- Use `ON CONFLICT DO NOTHING / DO UPDATE` for upserts (not `INSERT OR IGNORE/REPLACE`)
- PostgreSQL data types: `TIMESTAMPTZ` for dates, `SERIAL` for auto-increment, `BIGINT` for user IDs

### Telegram Messages

- Plain messages sent via `SendMessageAsync`: `ParseMode.MarkdownV2` — escape special characters with `MarkdownHelper.Escape(text, ParseMode.MarkdownV2)`
- Messages with inline/reply keyboards: `ParseMode.Markdown` — escape with `MarkdownHelper.Escape(text, ParseMode.Markdown)`
- Do not mix the two parse modes
- Completion notifications (via `NotificationSenderService`) are sent as **plain text** (no parse mode)
- All Telegram API methods must be current — do not use deprecated approaches

### TelegramBotHostedService — Graceful Shutdown

**Graceful drain (v1.7):** `_processingCts` is **not** linked to `stoppingToken` (was `CreateLinkedTokenSource(stoppingToken)`). This fix ensures buffered updates in the channel (up to 200) are processed before shutdown. Correct shutdown order:
1. `stoppingToken` cancels → `StartReceiving` stops polling.
2. `_updateChannel.Writer.TryComplete()` — no more updates enter the channel.
3. Await `processingTask` with 10s timeout — remaining updates are drained.
4. `_processingCts.CancelAsync()` — only if reader still active.

Previously, the linked CTS caused `ReadAllAsync(ct)` to stop immediately on shutdown, losing buffered updates.

### General

- XML doc comments (`/// <summary>`) on new interface methods
- Use `required` keyword on model properties that must always be set
- Prefer `??` and `?? throw new InvalidOperationException(...)` over unchecked null dereferences
- Maintain good code readability and unify methods for easier editing
- Extract shared static helpers (`HandlerHelpers`, `NpgsqlHelper`) when the same 5+ line pattern appears in multiple files
- Use `dotnet format --diagnostics IDE0005` to remove unused `using` directives
- Notification delivery is decoupled via `Channel<NotificationItem>` (256-capacity bounded channel): `CommandNotificationService` enqueues, `NotificationSenderService` dequeues and sends to Telegram.
- `NpgsqlHelper.CreateOpenConnectionAsync()` (static, in `TelegramBot.Data`) is the public helper for services that don't inherit from `DataAccessBase` (e.g., `CommandNotificationService`, `NotificationSenderService`).

### Optimization Principles

#### Каждый функционал — одна реализация
- ✅ `SendErrorAsync`/`SendNotificationAsync` — удалены
- ✅ `DefaultConnectionString` — вынесен в `DataAccessBase`
- ✅ Reconnect-циклы — вынесены в `PostgresReconnectLoop`
- ✅ `SessionDataService`/`CommandDataService` — двойная DI-регистрация (упрощена в v1.4)

#### Производительность
- ✅ **Batch-обработка Telegram:** заменить последовательный цикл на `Parallel.ForEachAsync`
- ✅ Markdown-экранирование: `Regex.Replace`
- ✅ Кэширование ФС: TTL-кэш (5 сек)
- ✅ Очистка сессий: lazy при доступе + фоновая раз в 30 мин

#### Интерфейсы
- ✅ Все single-implementation удалены (кроме `ITelegramOutputService`, data services)
- Не создавай новый интерфейс, если не планируется ≥2 реализаций.

---

## Known Issues (Do Not Worsen)

- `.editorconfig` exists with naming rules, formatting preferences, and `generated_code = true` markers for specific files — `dotnet format --verify-no-changes` passes with **exit code 0** (full compliance)
- **VSTHRD analyzer** (`Microsoft.VisualStudio.Threading.Analyzers`) is enabled globally via `Directory.Build.props` with `WarningsAsErrors` for 8 codes (VSTHRD002/003/100-104/200). All violations have been fixed or suppressed with justified `#pragma` — `dotnet build` produces **0 VSTHRD errors**
- CI pipeline exists (`.github/workflows/ci.yml`) — runs `dotnet build` and `dotnet publish` on push/PR. No automated tests — the only verification is a successful `dotnet build`
- Keep secrets out of committed config files — use `TelegramBot.Server/appsettings.Local.json` (gitignored) or env var `TelegramBot__Token`; never hardcode tokens
- PostgreSQL connection string in committed `appsettings.json` uses default `postgres/postgres` credentials — override via `appsettings.Local.json` or env var `ConnectionStrings__Postgres`
- **Interfaces remaining:** `ITelegramOutputService` — sole consumer interface.

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **TelegramBot** (1188 symbols, 3104 relationships, 97 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

> Index stale? Run `node .gitnexus/run.cjs analyze` from the project root — it auto-selects an available runner. No `.gitnexus/run.cjs` yet? `npx gitnexus analyze` (npm 11 crash → `npm i -g gitnexus`; #1939).

## Always Do

- **MUST run impact analysis before editing any symbol.** Before modifying a function, class, or method, run `impact({target: "symbolName", direction: "upstream"})` and report the blast radius (direct callers, affected processes, risk level) to the user.
- **MUST run `detect_changes()` before committing** to verify your changes only affect expected symbols and execution flows. For regression review, compare against the default branch: `detect_changes({scope: "compare", base_ref: "master"})`.
- **MUST warn the user** if impact analysis returns HIGH or CRITICAL risk before proceeding with edits.
- When exploring unfamiliar code, use `query({query: "concept"})` to find execution flows instead of grepping. It returns process-grouped results ranked by relevance.
- When you need full context on a specific symbol — callers, callees, which execution flows it participates in — use `context({name: "symbolName"})`.

## Never Do

- NEVER edit a function, class, or method without first running `impact` on it.
- NEVER ignore HIGH or CRITICAL risk warnings from impact analysis.
- NEVER rename symbols with find-and-replace — use `rename` which understands the call graph.
- NEVER commit changes without running `detect_changes()` to check affected scope.

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
