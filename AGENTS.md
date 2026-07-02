# AGENTS.md

Guidance for agentic coding agents working in this repository.

## Документация проекта

| Документ | Описание |
|----------|----------|
| [README.md](README.md) | Обзор проекта, запуск, конфигурация, команды бота |
| [Docs/ExecutionAlgorithm.md](Docs/ExecutionAlgorithm.md) | Спецификация алгоритма выполнения команд, схема БД, SQL-запросы |
| [Docs/BimPluginContract.md](Docs/BimPluginContract.md) | Контракт Revit AddIn, Navisworks/FileConvert и AI-исполнителей |
| [Docs/CriticalReview.md](Docs/CriticalReview.md) | Статус критичных замечаний и остаточные риски |
| [Docs/RevitCrashes.md](Docs/RevitCrashes.md) | 🔴 Расследование крашей Revit (`ACCESS_VIOLATION`) — симптомы, гипотезы, методы исправления |
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

- **TelegramBot.Core** — Models, DTOs, interfaces, config, constants, `RateLimiter`. Zero Telegram SDK dependency.
- **TelegramBot.Data** — **PostgreSQL 18** persistence via Dapper + Npgsql. References Core only. SQL constants in `Sql/` (6 partial files).
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

> **Обзор:** [README.md](README.md). **Алгоритм:** [Docs/ExecutionAlgorithm.md](Docs/ExecutionAlgorithm.md).

---

## Configuration

Подробная конфигурация с примерами — в [README.md](README.md#конфигурация).

- `TelegramBot.Server/appsettings.json` — committed, Serilog, `FileSystem`, `ConnectionStrings:Postgres`, `RateLimit`
- `TelegramBot.Server/appsettings.Local.json` — **gitignored**, secrets (bot token)
- `TelegramBot.Worker/appsettings.json` — committed, `ConnectionStrings:Postgres`, `BimIntegration`, `Worker`, `DialogDismisser`
- Required keys: `TelegramBot:Token`, `TelegramBot:AdminUserIds`, `FileSystem:RootPath`, `ConnectionStrings:Postgres`, `RateLimit:MaxFilesPerUserPerDay`, `Worker:CompletedSessionRetentionDays`

---

## Architecture & Request Flow

```
Telegram API -> TelegramBotHostedService (long-polling, parallel processing)
             -> Update channel (bounded 200) -> Parallel.ForEachAsync (MaxDegree=10)
             -> TelegramUpdateMapper (Update -> MessageDto | CallbackQueryDto)
             -> per-user SemaphoreSlim (SessionManager.AcquireUserLockAsync)
             -> CommandAppService.HandleUserCommandAsync (text commands)
                ├── /start bypasses access check → registration or help
                └── other commands → BotUsers.Status must be Approved
             -> CommandAppService.HandleCallbackAsync (inline keyboard callbacks)
                ├── REQACCESS/APPROVEUSER/REJECTUSER bypass access check
                └── all other callbacks → user must be Approved
                → CallbackDispatcher (O(1) prefix→handler map)
                   → ICallbackHandler chain
```

### TelegramBotHostedService — параллельная обработка

`TelegramBotHostedService` использует `Channel<Update>` (capacity 200) и `Parallel.ForEachAsync` с `MaxDegreeOfParallelism = 10`:

- SDK `StartReceiving` кладёт обновления в канал (`HandleUpdateAsync` → `WriteAsync`)
- `ProcessUpdatesAsync` параллельно читает и обрабатывает (`MaxConcurrentUpdates = 10`)
- Per-user блокировка через `SessionManager.AcquireUserLockAsync` гарантирует, что обновления одного пользователя обрабатываются последовательно

### Server Components

| Слой | Компонент | Роль |
|------|-----------|------|
| Infrastructure | `TelegramBotHostedService` | Long-polling + параллельная обработка + graceful shutdown |
| Infrastructure | `TelegramUpdateMapper` | `Update` → `MessageDto`/`CallbackQueryDto` |
| Infrastructure | `TelegramOutputService` | Обёртка над `ITelegramBotClient`: `SendMessageAsync`, `EditMessageReplyMarkupAsync`, `AnswerCallbackAsync`, `SendChatActionAsync`, `SendMessageWithReplyKeyboardAsync`, `RemoveReplyKeyboardAsync`. HTTP 429 retry |
| Infrastructure | `KeyboardBuilder` | Inline- и reply-клавиатуры: секции, команды, фильтры `/status`, сессии |
| Infrastructure | `FileSystemBrowser` | Навигация по `RootPath` → проекты → `01_PROJECT/<project>/<раздел>/` → `01_RVT/*.rvt` |
| Infrastructure | `CommandNotificationService` | `BackgroundService`: слушает PostgreSQL `LISTEN command_completed` и `LISTEN session_started`; `session_started` ставит direct item в `Channel<NotificationItem>`, `command_completed` ставит wake-up для outbox drain |
| Infrastructure | `NotificationSenderService` | `BackgroundService`: шлёт "⚙️ Задание запущено" из channel; completion-сводки читает из `NotificationOutbox` при старте, по wake-up и polling каждые 30 сек; после Telegram send помечает запись `sent` |
| Application | `CommandAppService` | Rate-limit → session creation → post-restart cleanup → `SlashCommandService` |
| Application | `SlashCommandService` | Обработка `/start`/`/export`/`/automation`/`/status`/`/help`, кнопок `Apply`/`Confirm`/`Cancel`. Сканирует `01_RVT/*.rvt` через `RevitFileDeduplicator`, проверяет daily limit, вставляет сессию в БД. Показывает индикатор «печатает…» |
| Application | `RevitFileDeduplicator` | Удаляет дубликаты RVT-файлов: exact-name dedup + grouping по первым 15 символам имени + numeric-token overlap (короткое имя выигрывает) |
| Application | `CallbackDispatcher` | O(1) lookup префикса → handler (кэшированный `Dictionary<prefix, handler>`) |
| Application | `SessionManager` | `ConcurrentDictionary<long, UserSession>`. Per-user `SemaphoreSlim` для сериализации обновлений. Lazy + background cleanup (раз в 30 мин). Безопасное удаление семафоров: проверка `CurrentCount == 1` |
| Application | `DataServices` | Aggregate-обёртка: `Sessions` / `Commands` / `MessageTracking` — устраняет двойную DI-регистрацию |
| Application | `SessionsListRenderer` | Рендеринг списка сессий для `/status` с фильтрацией и нумерацией |
| Middleware | `AuthorizationMiddleware` | Валидация доступа, `BypassesAccessCheck` для access-related callback-ов, optimistic refresh админа через `UpdatedAt` |
| Handlers | `AccessRequestHandler` (P=0) | `REQACCESS:`, `APPROVEUSER:`, `REJECTUSER:` — отправляет запрос всем админам |
| Handlers | `FileNavigationHandler` (P=10) | `GOTOPARENT:` — навигация в выбранную папку с проверкой `IsPathWithinRoot` |
| Handlers | `FileSelectionHandler` (P=20) | Тоггл выбора файла/папки |
| Handlers | `CommandToggleHandler` (P=100) | Тоггл выбора команды (`PDF:`, `DWG:`, `IFC:` и т.д.) |
| Handlers | `SessionManagementHandler` (P=100) | `SESSIONDETAILS:`, `DELETESESSION:`, `DELETECOMMAND:`, `CONFIRMDELETESESSION:`, `CONFIRMDELETECOMMAND:`, `DELETESESSIONBYTYPE:`, `CONFIRMDELETESESSIONBYTYPE:`, `STATUSFILTER:`, `STATUSPAGE:`, `CMDPAGE:` |
| Handlers | `CommandSelectionHandler` (P=100) | `APPLYCOMMANDS:`, `CANCELCOMMANDSSEL:` |
| Helpers | `HandlerHelpers` | `SendActionsReplyKeyboardAsync` (общий для SlashCommandService, FileNavigationHandler, CommandSelectionHandler) |
| Helpers | `MarkdownHelper` | `Escape(text, ParseMode)` для Markdown/MarkdownV2 |
| Config | `BotCommandsSetup` | `ConfigureAsync` — устанавливает список команд бота через Telegram API |

**DI registration** — в `TelegramBot.Server/Extensions/DependencyInjectionExtensions.cs`:

```csharp
.AddCallbackHandlers()      // 6 ICallbackHandler + CallbackDispatcher
.AddApplicationServices()   // CommandAppService, AuthorizationMiddleware, RateLimiter, SlashCommandService, SessionManager
.AddInfrastructureServices()// DataServices, DatabaseInitializerService, FileSystemBrowser, NotificationOutboxDataService
.AddTelegramServices()      // ITelegramBotClient, ITelegramOutputService, TelegramUpdateMapper, KeyboardBuilder, Channel<NotificationItem>, 3 hosted services

**Server DI registration (DependencyInjectionExtensions.cs):**

```csharp
.AddConfiguration()          // FileSystemOptions, BotOptions, RateLimitOptions
.AddCallbackHandlers()       // 6 ICallbackHandler + CallbackDispatcher
.AddApplicationServices()    // CommandAppService, AuthorizationMiddleware, RateLimiter, SlashCommandService, SessionManager, SessionsListRenderer
.AddInfrastructureServices() // UserDataService, CommandDataService, SessionDataService, MessageTrackingDataService, NotificationOutboxDataService, DataServices, DatabaseInitializerService, FileSystemBrowser
.AddTelegramServices()       // ITelegramBotClient, ITelegramOutputService, TelegramUpdateMapper, KeyboardBuilder, Channel<NotificationItem>, TelegramBotHostedService, CommandNotificationService, NotificationSenderService
```
```

### BimLib (BIM Integration) — embedded in Worker

BimLib is a **Windows-only** set of modules located inside the Worker project (`TelegramBot.Worker/BimLib/`). It provides BIM-related infrastructure used by `CommandExecutionService` and `CommandPreparer`. Marked `[SupportedOSPlatform("windows")]` and assembled via `[assembly: SupportedOSPlatform("windows")]` in `Worker/Program.cs`.

**Structure:**

| Folder | Contents |
|--------|----------|
| `Config/` | `BimIntegrationOptions` — min/max supported Revit version (default 2018–2026); legacy `RevitInstallRoot` сейчас не используется, поиск идёт через реестр. `DialogDismisserOptions` — `Enabled`, `MaxDismissAttempts`, `KnownDialogPatterns`, `CloseButtonTexts`, `ExclusionDialogTitles` |
| `Models/` | `RevitDetectedVersion` (Year, ExecutablePath), `RevitProcessHealth` (status: `Healthy`/`NotResponding`/`Error`, MemoryMb, Duration) |
| `Monitor/` | `ProcessHealthHelper` (static `CheckHealth`), `DialogDismisser`, `WindowUtil`, `WindowInfo` |
| `Native/` | P/Invoke WinAPI: `User32`, `Win32Types`, `WinApiHelper` (с настраиваемым `ILogger` для safe error logging) |
| `Services/` | `RevitVersionDetector`, `RevitPathResolver`, `NavisworksPathResolver` |

**Key services:**

| Service | Role |
|---------|------|
| `RevitVersionDetector` | Читает OLE-стрим `BasicFileInfo` из .rvt/.rfa через OpenMcdf, извлекает `Format: YYYY`. Поддерживает `.rvt`, `.rfa`, `.rte`. Возвращает `RevitDetectedVersion` с `Year` (2017–2026 supported) и `ExecutablePath` от `RevitPathResolver` |
| `RevitPathResolver` | Резолвит `Revit.exe` через реестр Windows: `HKLM\SOFTWARE\Autodesk\Revit\{version}` (fallback `\Revit{version}` и `WOW6432Node`), ищет подраздел с префиксом `REVIT-`, читает `InstallationLocation` |
| `NavisworksPathResolver` | Резолвит `FileConvert.exe` / `Roamer.exe` / `Navisworks.exe`. Реестр: `HKLM\SOFTWARE\Autodesk\Navisworks\R{year}` или `NavisworksManage\R{year}` |
| `RevitProcessTracker` | _Удалён в v1.x — мониторинг Revit делегирован `ProcessHealthHelper` (вызывается из `CommandExecutionService.CheckProcessesHealth`)._ |
| `NavisworksProcessTracker` | _Удалён в v1.x — `CommandExecutionService` использует `ProcessHealthHelper` напрямую._ |
| `ProcessHealthHelper` | Статический `CheckHealth(Process, ILogger, context)`: `RevitingResponseStatus` по IsResponding + memory sampling |
| `DialogDismisser` | Авто-закрывает модальные окна Revit/Navisworks (#32770): ищет окна по `KnownDialogPatterns`, нажимает кнопки из `CloseButtonTexts`. Исключает информационные диалоги (`ExclusionDialogTitles`). ⚠️ **Временно отключён** (`DialogDismisser:Enabled = false`) для тестирования на реальных задачах — проверяется гипотеза о крашах Revit (`ACCESS_VIOLATION`) от P/Invoke; при `Enabled=false` метод делает ранний `return` до любых Win32-вызовов |

**DI registration** в `Worker/Program.cs`:
```csharp
services.AddSingleton<RevitVersionDetector>();
services.AddSingleton<RevitPathResolver>();
services.AddSingleton<DialogDismisser>();
services.AddSingleton<NavisworksPathResolver>();
```
Требует `BimIntegrationOptions` (секция `BimIntegration`) и `DialogDismisserOptions` (секция `DialogDismisser`) в `appsettings.json`.

**Namespaces:**
- `TelegramBot.Worker.BimLib.Config`
- `TelegramBot.Worker.BimLib.Models`
- `TelegramBot.Worker.BimLib.Monitor`
- `TelegramBot.Worker.BimLib.Native`
- `TelegramBot.Worker.BimLib.Services`

### Worker Components

`CommandExecutionService` — slim orchestrator, делегирующий на:

| Component | Role |
|-----------|------|
| `CommandDataService` | `ClaimPendingCommandsAsync` (FOR UPDATE SKIP LOCKED + Lease), `UpdateCommandStatusAsync`, `MarkProcessStartedAndNotifyOnceAsync` (ProcessId + idempotent `session_started` pg_notify), `ScheduleRetryAsync` (NextRetryAt + RetryCount), `ReleaseExpiredLeasesAsync` (advisory lock), `HasDuplicateCommandsAsync`, `DeleteCommandAsync`, `DeleteCommandsByTypeAsync` |
| `CommandPreparer` | `PrepareAsync`: 1) `Commands.TryGetValue` → Fail если unknown, 2) `ValidateFilePath` (path traversal, reparse-point, extension, root containment), 3) `ResolveExecutablePathAsync` (PDF/DWG/IFC/BIMDOC/NWC через Revit BimLib, CLASHREP через Navisworks BimLib, fallback на configured path). Создаёт **копию `CommandConfig`** перед `ExecutablePath` mutation. `CreateTaskFile` (atomic write `.tmp` → `Move`), `CreateProcessStartInfo` (substitutes `{CommandText}`/`{CommandId}`/`{TaskFilePath}`/`{ResultFilePath}`; Revit AddIn получает fixed dispatcher `WORKER` (`WorkerOptions.RevitDispatcherCommand`), **без `{FilePath}`** — путь к `.rvt` только в TaskFile), `CleanupTempFiles` |
| `ProcessRunner` | `RunAsync(cmd, ct)`: генерирует `attemptToken` (GUID без дефисов) → `PrepareAsync` → `StartProcessAsync` (создаёт task-файл, запускает процесс через **`_launchGate` SemaphoreSlim** с `LaunchStaggerSeconds` паузой для предотвращения коллизий CEF-порта Revit, регистрирует в `_activeProcesses` **до** stagger-задержки, пишет ProcessId и идемпотентно шлёт `session_started` через `MarkProcessStartedAndNotifyOnceAsync`) → `WaitAndHandleResultAsync` (OutputDataReceived + ErrorDataReceived с 64KB лимитом + `truncated` флаг) → `TryReadResultFile` (parse → delete на success; rename в `.bad` на битый XML) → `HandleTimeoutAsync` или `HandleFailureAsync`. `ActiveProcesses` — snapshot для health-мониторинга |
| `SessionCompletionTracker` | `OnCommandCompletedAsync`: `CountPendingProcessingBySessionAsync` (DB confirm) → `NotifySessionCompletedOnceAsync` → `CompletionNotified=TRUE` + `NotificationOutbox` insert + `pg_notify('command_completed')` wake-up |
| `SessionCleanupService` | `BackgroundService`: soft-delete сессий старше `CompletedSessionRetentionDays` без pending/processing. `0` отключает |

**Drain loop (v1.7):** `CommandExecutionService.DrainPendingCommandsAsync` — claim'ит пачку (`DefaultBatchSize = 5`), запускает каждую команду как background `Task`, сразу пытается claim'ить ещё. Это устраняет head-of-line blocking, когда одна долгая команда (3h Revit timeout) блокирует остальные 4 из батча. Трекинг выполняемых задач через `_runningTasks` (HashSet + lock) для корректного shutdown.

**Shutdown order (v1.7):**
1. `_shutdownCts.CancelAsync()` — останавливает background loops
2. `LogActiveProcessesOnShutdown()` + параллельный `Kill(entireProcessTree: true)` всех активных процессов в общем shutdown-бюджете (`30s`, per-process `10s`)
3. Wait for cleanup + health tasks (`15s` каждый)
4. Wait for running tasks (`15s`)
5. Dispose semaphores

**LaunchStaggerGate:** В `ProcessRunner` добавлен `SemaphoreSlim _launchGate` (capacity 1), сериализующий
момент `Process.Start()` для всех типов команд. После `Start()` gate удерживается `LaunchStaggerSeconds`
(default 30), чтобы встроенный CEF-компонент Revit успел забиндить devtools-порт. `Task.Delay` использует
`CancellationToken.None`, чтобы gate всегда освобождался даже при shutdown. Процесс регистрируется в
`_activeProcesses` **до** stagger-задержки, чтобы health-check и shutdown-kill видели его сразу.

**Корреляция событий:** каждая команда и сессия имеют `CorrelationId` (GUID без дефисов), который проходит через весь pipeline: создание сессии → claim → notify → completion → Telegram. Используется в логах для трассировки.

**DI registration** в `Worker/Program.cs`:
```csharp
services.AddSingleton<UserDataService>();
services.AddSingleton<CommandDataService>();
services.AddSingleton<SessionDataService>();
services.AddSingleton<MessageTrackingDataService>();
services.AddSingleton<CommandPreparer>();
services.AddSingleton<SessionCompletionTracker>();
services.AddSingleton<ProcessRunner>();
services.AddHostedService<CommandExecutionService>();
services.AddHostedService<SessionCleanupService>();
```

**DataAccessBase:** общий base-класс для `UserDataService`/`CommandDataService`/`SessionDataService`/`MessageTrackingDataService`/`NotificationOutboxDataService` с protected `CreateOpenConnectionAsync()`. Public static `NpgsqlHelper.CreateOpenConnectionAsync()` — для `CommandNotificationService`, который НЕ наследует `DataAccessBase`.

---

## Important notes for AI agents

- BimLib is `[SupportedOSPlatform("windows")]` — Windows only (Registry + P/Invoke). OpenMcdf 3.x парсит .rvt OLE streams. Worker и Server тоже помечены — Server дополнительно делает runtime check `RuntimeInformation.IsOSPlatform(OSPlatform.Windows)`.
- `RevitVersionDetector` извлекает год из `BasicFileInfo`; допустимый для запуска диапазон задают `BimIntegrationOptions.MinSupportedVersion`/`MaxSupportedVersion` (default `2018`–`2026`).
- `RevitProcessStatus` enum: `Healthy`, `NotResponding`, `Error`.
- Removed BimLib interfaces: `IRevitPathResolver`, `IRevitProcessTracker`, `INavisworksProcessTracker`, `IRevitVersionDetector`, `INavisworksPathResolver` (concrete-классы only).
- Команды помечены priority через `SlashCommandService._commandPriorityMap` (`FrozenDictionary<string, int>`): PDF=Critical(1), DWG=High(2), NWC/IFC/BIMDOC/CLASHREP=Medium(3), AUTORES=Low(4), default=Default(5).
- `WorkerOptions.RevitDispatcherCommand` = `"WORKER"` — константа-диспетчер для Revit AddIn. Реальная команда (`PDF`, `DWG`, ...) передаётся только в `TaskFile.commandText`, не в CLI args.
- `WorkerOptions.LaunchStaggerSeconds` (default `30`) — пауза между запусками внешних процессов для предотвращения коллизии CEF devtools-порта Revit. `0` отключает.
- `SessionManager.GetOrCreateSession()` no longer calls `RemoveSession()` (была race с `AcquireUserLockAsync`). Background `CleanUpExpiredSessionsAsync` безопасно обрабатывает оба dictionary.

### How BIM Command Plugins Actually Work

Полный контракт исполнителей описан в [Docs/BimPluginContract.md](Docs/BimPluginContract.md).

> ⚠️ **CANONICAL CONTRACT (эталон)** находится в:
> `C:\Users\y.zhumabayev\Repository\RevitBIMFusion\Docs\BimPluginContract.md`
> + XSD-схемы `TaskFile.schema.xsd` / `ResultFile.schema.xsd` рядом с ним.
>
> [Docs/BimPluginContract.md](Docs/BimPluginContract.md) — **worker-side отражение** этой границы. **Реализация полностью соответствует эталону.** При изменениях в `TaskFile` / `ResultFile` / `Worker:Commands:ArgumentsTemplate` / `CommandPreparer.CreateTaskFile` / `ProcessRunner.TryReadResultFile` **обязательно** сверяйся с эталоном и обновляй эталон + плагин + код **синхронно**.

Кратко: Worker запускает внешний процесс и обменивается с ним через XML-файлы в **TaskDirectory** (по умолчанию `%USERPROFILE%\Documents\TelegramBot\TaskDirectory\`, настраивается через `FileSystem:TaskDirectory`):

| Файл | Кто создаёт | Кто читает | Назначение |
|------|------------|------------|------------|
| `task_{CommandId}_{AttemptToken}.xml` | Worker (`CommandPreparer.CreateTaskFile`, atomic write) | Плагин | Задание: что и с каким файлом делать |
| `result_{CommandId}_{AttemptToken}.xml` | Плагин (atomic write) | Worker (`ProcessRunner.TryReadResultFile`) | Результат: успех/ошибка/отмена + выходные файлы |

**TaskFile** (`TelegramBot.Core.Models.TaskFile`):
```xml
<taskFile>
  <commandId>42</commandId>
  <commandText>PDF</commandText>
  <filePath>B:\project.rvt</filePath>
  <resultFilePath>C:\Users\svc\Documents\TelegramBot\TaskDirectory\result_42_6f1c2b3a.xml</resultFilePath>
</taskFile>
```
- `commandId` — ID команды в БД
- `commandText` — тип экспорта (`PDF`, `DWG`, `IFC`, `BIMDOC`, `NWC`, `CLASHREP`, `AUTORES`)
- `filePath` — полный путь к исходному файлу. AddIn открывает его сам через `OpenOptions { Audit = true, DetachAndPreserveWorksets }`. **Не передаётся в CLI args** (только в TaskFile).
- `resultFilePath` — путь в **TaskDirectory**, куда плагин должен записать результат
- `options` — XML element (closed whitelist; поддерживается только `continueOnError` для PDF/DWG)

**ResultFile** (`TelegramBot.Core.Models.ResultFile`):
```xml
<resultFile>
  <status>done</status>
  <outputFiles>B:\project.pdf</outputFiles>
</resultFile>
```
- `status` — enum `ResultStatus { Done, Failed, Cancelled }`, XML value `done`/`failed`/`cancelled`
- `errorMessage` — короткое сообщение об ошибке (при `failed`/`cancelled`)
- `errorDetails` — полный stack trace (для неожиданных исключений)
- `outputFiles` — `string?` (путь к выходному файлу при `done`; несмотря на множественное число в имени — **одна строка**, не массив, соответствует канону в `…\RevitBIMFusion\Docs\ResultFile.schema.xsd`)

**Алгоритм:**
1. Worker генерирует `attemptToken` (GUID без дефисов) для каждой попытки
2. `CommandPreparer.CreateTaskFile` пишет `task_{CommandId}_{attemptToken}.xml` в **TaskDirectory** (atomic: `.tmp` → `File.Move(overwrite: true)`). По умолчанию `%USERPROFILE%\Documents\TelegramBot\TaskDirectory\` (рядом с `Logs\Worker\`), настраивается через `FileSystem:TaskDirectory`. Не используется `Path.GetTempPath()` — иначе Windows-cleaner'ы могут удалить файлы во время длительной команды.
3. Worker запускает `Revit.exe`/`FileConvert.exe`/`python` с аргументами из `ArgumentsTemplate` (подстановка `{CommandText}`/`{TaskFilePath}`/`{ResultFilePath}`). Для Revit AddIn `args[2]` всегда `WORKER`, реальная команда берётся из `TaskFile.commandText`. **Без `{FilePath}`** — путь к `.rvt` передаётся только через TaskFile (см. эталон §CLI Arguments).
4. Исполнитель читает task-файл, выполняет команду, пишет `result_{CommandId}_{attemptToken}.xml` в ту же TaskDirectory по пути из `resultFilePath` (атомарно: `.tmp` → `File.Move`)
5. `ProcessRunner.WaitAndHandleResultAsync` собирает stdout/stderr через `OutputDataReceived` (лимит 64KB на каждый, флаг `truncated` в логах), затем:
   - `status="done"` → `Done`
   - `status="failed"` → `HandleFailureAsync` (retry/error classification); `errorMessage` в лог, `errorDetails` в Debug-лог
   - `status="cancelled"` → permanent `Failed` без retry
   - Битый XML / unreadable / неизвестный status → rename в `.bad` → `HandleFailureAsync`
   - result XML отсутствует — fallback по exit code: `0` = `Done`, иначе `HandleFailureAsync`
6. Файлы текущей попытки очищаются в `finally` блока `ProcessRunner.RunAsync()` (через `CommandPreparer.CleanupTempFiles`)

Исполнитель должен записать result-файл и завершиться с exit code `0` при успехе. Если result-файл не найден — Worker использует fallback по exit code.

#### Command-line arguments

Плагин получает аргументы командной строки (шаблон `ArgumentsTemplate` в `appsettings.json`):

```
Revit.exe /command "WORKER" "C:\Temp\task_42_6f1c2b3a.xml"
```

Доступные плейсхолдеры:
| Плейсхолдер | Описание |
|-------------|----------|
| `{CommandText}` | Тип экспорта для console/wrapper-команд; для Revit AddIn не используется как dispatcher |
| `{FilePath}` | Полный путь к исходному файлу |
| `{CommandId}` | ID команды в БД |
| `{TaskFilePath}` | Полный путь к `task_{CommandId}_{AttemptToken}.xml` |
| `{ResultFilePath}` | Полный путь к `result_{CommandId}_{AttemptToken}.xml` |

**Рекомендуемый подход:** плагин должен читать task-файл, а не полагаться только на аргументы командной строки — XML содержит полную структурированную информацию.

> **Важно (v1.7):** Имена temp-файлов включают уникальный `AttemptToken` (GUID без дефисов) для каждой попытки выполнения. Это предотвращает: (1) подсовывание ложного result локальным процессом (predictable filenames), (2) чтение stale result от предыдущей retry-попытки, (3) конфликты между параллельными выполнениями одной команды.

#### Revit without AddIn (broken flow):

```
Worker → Revit.exe opens as GUI
         ↓
         Revit just sits there, showing an empty project
         ↓
         3 hours later → Worker kills it → Command timed out → Failed
```

`DialogDismisser` только закрывает известные модальные окна Revit/Navisworks. Без Revit AddIn Revit не знает что делать с `/command` и просто открывается GUI, игнорируя аргументы.

#### Other command types:

| Type | Executable | stdout/stderr | Result mechanism |
|------|-----------|---------------|------------------|
| PDF, DWG, IFC, BIMDOC, NWC | `Revit.exe` + Revit AddIn | **No** — GUI app | TaskFile + ResultFile XML exchange |
| CLASHREP | `FileConvert.exe`/Navisworks wrapper | **Usually yes** for CLI wrapper | TaskFile + ResultFile if wrapper/plugin supports it; otherwise fallback to exit code |
| AUTORES | `python ai_agent.py` | **Yes** — console script | TaskFile + ResultFile via `--task`/`--result`, fallback to exit code |

**Temp-file cleanup (v1.7):** Temp-файлы (`task_*.xml`, `result_*.xml`) очищаются per-attempt в `finally` блоке `ProcessRunner.RunAsync()`. Каждая попытка использует уникальный `attemptToken`, предотвращая stale-file конфликты между retry.

### Shared Static Helpers

| Helper | Location | Purpose |
|--------|----------|---------|
| `HandlerHelpers` | `Server/Services/Application/Handlers/HandlerHelpers.cs` | `SendActionsReplyKeyboardAsync()` — общий reply-keyboard + tracking для SlashCommandService, FileNavigationHandler, CommandSelectionHandler |
| `ProcessHealthHelper` | `Worker/BimLib/Monitor/ProcessHealthHelper.cs` | `CheckHealth()` — проверка активных внешних процессов из `CommandExecutionService` |
| `NpgsqlHelper` | `TelegramBot.Data/NpgsqlHelper.cs` | `CreateOpenConnectionAsync()` (public static) — для сервисов, не наследующих `DataAccessBase` (`CommandNotificationService`) |
| `ErrorClassifier` | `TelegramBot.Worker/Services/ErrorClassifier.cs` | `IsPermanentFailure(message, exitCode, codes, exception)`: классификация ошибок → permanent (Failed без retry) vs transient (retry) |
| `CommandPreparer.CleanupTempFiles` | `TelegramBot.Worker/Services/CommandPreparer.cs` | Best-effort удаление `task_{CommandId}_{token}.xml` и `result_{CommandId}_{token}.xml` для указанной попытки |
| `DataAccessBase` | `TelegramBot.Data/DataAccessBase.cs` | Base-класс с protected `CreateOpenConnectionAsync()`, `DefaultConnectionString` |

---

## Constants Reference

All constants are located in `TelegramBot.Core/Constants/`. Use these instead of hardcoded strings/ints.

| File | Purpose | Key Constants |
|------|---------|---------------|
| `CallbackPrefixes.cs` | Inline keyboard callback prefixes | `GoToParent`, `File`, `Pdf`/`Dwg`/`Nwc`/`Ifc`/`BimDoc`/`ClashRep`/`AutoRes`, `SessionDetails`, `DeleteSession`, `DeleteCommand`, `ConfirmDeleteSession`, `ConfirmDeleteCommand`, `DeleteSessionByType`, `ConfirmDeleteSessionByType`, `RequestAccess`, `ApproveUser`, `RejectUser`, `StatusFilter`, `StatusPage`, `CommandsPage`, `SelectAllSectionFolders`, `ApplyCommands`, `CancelCommandSelection` |
| `CommandCodes.cs` | Export command identifiers | `Pdf`, `Dwg`, `Nwc`, `Ifc`, `BimDoc`, `ClashRep`, `AutoRes` |
| `Statuses.cs` | Entity statuses (commands/sessions) | `Pending`, `Processing`, `Done`, `Failed`, `Deleted`, `FinalStatuses` (set), `ActiveStatuses` (set) |
| `CommandPriorities.cs` | Worker queue priority levels | `Critical`=1, `High`=2, `Medium`=3, `Low`=4, `Default`=5 |
| `ButtonTexts.cs` | Reply keyboard button labels | `Apply` ("✅ Применить"), `Confirm` ("✅ Подтвердить"), `Cancel` ("❌ Отмена") |

**Server-only constants** (`TelegramBot.Server/Constants/`):
- (нет — dispatcher полагается на уникальность префиксов между хендлерами, см. `CallbackPrefixes`)

**Important notes:**
- All callback prefixes end with `:` (colon) for data concatenation
- `Statuses.FinalStatuses` = `{Done, Failed, Deleted}` — терминальные статусы
- `Statuses.ActiveStatuses` = `{Pending, Processing}` — активные статусы
- Command codes match callback prefix names (e.g., `CommandCodes.Pdf` = `"PDF"`, `CallbackPrefixes.Pdf` = `"PDF:"`)
- Button texts include emoji и используются с reply keyboards (не inline)
- `CommandPriorities.Critical` (1) выше `High` (2) и т.д.; **меньше значение = выше приоритет**

### Task Execution Flow (Server → PostgreSQL → Worker)

Полная спецификация алгоритма: **[Docs/ExecutionAlgorithm.md](Docs/ExecutionAlgorithm.md)**

```
SlashCommandService.ConfirmFileSelectionAsync()
    │
    ├── RevitFileDeduplicator.Deduplicate()  // exact-name + 15-char prefix group + numeric-token overlap
    ├── RateLimiter (CheckDailyFileLimitAsync: CountQueuedFilesByUserSinceAsync)
    ├── CommandDataService.HasDuplicateCommandsAsync (active queue dedup)
    ├── dataService.CreateSessionWithCommandsAsync() -- INSERT INTO Sessions + Commands + pg_notify('new_tasks', correlationId)
    │
    ▼
CommandNotificationService (Server) ── LISTEN session_started ──▶ Channel<NotificationItem> ──▶ NotificationSenderService ──▶ "⚙️ Задание запущено" пользователю

CommandExecutionService (Worker)
    LISTEN/NOTIFY new_tasks (мгновенная реакция) + fallback polling (5 мин)
    DrainPendingCommandsAsync:
        ClaimPendingCommandsAsync(limit) -- FOR UPDATE SKIP LOCKED + Lease
        ProcessWithPoolAsync(cmd):
            _commandSlots.WaitAsync(ct)
            ProcessRunner.RunAsync(cmd):
                CommandPreparer.PrepareAsync (validation + BimLib path resolution + Config clone)
                CreateTaskFile (atomic write)
                StartProcessAsync (process start + MarkProcessStartedAndNotifyOnceAsync → ProcessId + StartNotified + pg_notify('session_started'))
                WaitAndHandleResultAsync (OutputDataReceived 64KB + TryReadResultFile):
                    status="done" → UpdateStatus=Done
                    status="failed" / битый / нет файла + exit != 0 → ErrorClassifier → permanent (Failed) / transient (ScheduleRetry)
                    exit=0 без файла → Done
            _commandSlots.Release()
            SessionCompletionTracker.OnCommandCompletedAsync:
                CountPendingProcessingBySessionAsync (DB confirm) → NotifySessionCompletedOnceAsync
    CleanupTempFiles in finally (per-attempt)

NotifySessionCompletedOnceAsync ──▶ Sessions.CompletionNotified=TRUE + INSERT NotificationOutbox(session_completed) + pg_notify('command_completed') wake-up
CommandNotificationService (Server) ── LISTEN command_completed ──▶ Channel<NotificationItem> wake-up
NotificationSenderService ──▶ Claim NotificationOutbox ──▶ SessionDataService.GetSessionCompletionSummaryAsync() ──▶ SendMessageAsync ──▶ Mark outbox sent
```

**Проверка завершения сессии:** после каждого выхода команды из `processing` (`Done`/`Failed`/retry) `SessionCompletionTracker` запрашивает `CountPendingProcessingBySessionAsync`. Уведомление создаётся только когда БД подтверждает отсутствие `pending`/`processing`; in-memory счётчика нет.

**Идемпотентность через `Sessions.CompletionNotified` + `NotificationOutbox`:** `SessionDataService.NotifySessionCompletedOnceAsync` атомарно выставляет `CompletionNotified = TRUE`, вставляет `NotificationOutbox(EventType='session_completed')` и отправляет `pg_notify('command_completed')` как wake-up только для первой успешной попытки (`UPDATE ... WHERE CompletionNotified = FALSE RETURNING SessionId`). Итоговая Telegram-сводка отправляется из durable outbox, а не напрямую из `NOTIFY`.

DI is wired in `TelegramBot.Server/Extensions/DependencyInjectionExtensions.cs`. The filesystem root comes from `FileSystemOptions` (bound to `"FileSystem"` config section). The Worker registers data services напрямую в `Program.cs`.

**Key DI simplification (v1.4):** все single-implementation интерфейсы удалены — consumers зависят от concrete-классов напрямую:
- `IFileSystemBrowser` → `FileSystemBrowser`
- `ITelegramUpdateMapper` → `TelegramUpdateMapper`
- `ICallbackDispatcher` → `CallbackDispatcher`
- `ICommandAppService` → `CommandAppService`
- `IDatabaseInitializer` → `DatabaseInitializerService`
- `ISlashCommandService` → `SlashCommandService`
- `IAccessValidator` → `AuthorizationMiddleware`
- `ISessionManager` → `SessionManager`
- `IRevitVersionDetector` → `RevitVersionDetector`
- `INavisworksPathResolver` → `NavisworksPathResolver`

### Smart Retry — ErrorClassifier

`ProcessRunner.HandleFailureAsync` использует `ErrorClassifier` для классификации ошибок:

| Тип | Поведение | Примеры |
|-----|-----------|--------|
| `InvalidFileError` (permanent) | Сразу `Failed`, без retry | Файл не найден, нет доступа, неверный формат, invalid path |
| `ProcessCrashError` (transient) | retry с экспоненциальной задержкой (`RetryDelayBaseSeconds * 2^(attempt-1)`, default 60s → 120s → 240s → 480s → 960s) | Процесс упал с неспецифичным кодом ошибки |

**Критерии permanent:**
1. **По тексту ошибки** — паттерны (EN+RU): `"not found"`, `"no such file"`, `"cannot open file"`, `"access denied"`, `"access is denied"`, `"invalid file"`, `"permission denied"`, `"path not found"`, `"no such directory"`, `"cannot access"`, `"файл не найден"`, `"путь не найден"`, `"отказано в доступе"`, `"доступ запрещен"`, `"нет доступа"`, `"недопустимый файл"`, `"неверный формат файла"`, `"невозможно открыть файл"`
2. **По exit code** — если входит в `WorkerOptions.PermanentFailureExitCodes` (HashSet, по умолчанию пуст)
3. **По типу исключения** — `FileNotFoundException`, `DirectoryNotFoundException`, `UnauthorizedAccessException`, `PathTooLongException`

**Config:** `WorkerOptions.PermanentFailureExitCodes` (`HashSet<int>`, по умолчанию пуст).
**MaxRetries:** `WorkerOptions.MaxRetries` (default 5) — после исчерпания → `Failed`.
**Namespace:** `TelegramBot.Worker.Services.ErrorClassifier`.

### Callback Handling — Chain of Responsibility

`CallbackDispatcher` routes callbacks to handler по уникальному `prefix` через кэшированный `_handlerMap` (`Dictionary<prefix, handler>`), построенный в конструкторе из `handlers.GetSupportedPrefixes()`. Все handlers extend `CallbackHandlerBase`. Префиксы между хендлерами не пересекаются (см. `CallbackPrefixes`), поэтому никакая сортировка/приоритеты не нужны.

Handler hierarchy (порядок не имеет значения — выбор по prefix):
- `AccessRequestHandler` — `REQACCESS:`, `APPROVEUSER:`, `REJECTUSER:`
- `FileNavigationHandler` — `GOTOPARENT:`
- `FileSelectionHandler` — `FILE:`, `SELECTALLSECTIONS:`
- `CommandToggleHandler` — `PDF:`, `DWG:`, `IFC:`, `BIMDOC:`, `NWC:`, `CLASHREP:`, `AUTORES:`
- `CommandSelectionHandler` — `APPLYCOMMANDS:`, `CANCELCOMMANDSSEL:`
- `SessionManagementHandler` — `SESSIONDETAILS:`, `DELETESESSION:`, `DELETECOMMAND:`, `CONFIRMDELETESESSION:`, `CONFIRMDELETECOMMAND:`, `DELETESESSIONBYTYPE:`, `CONFIRMDELETESESSIONBYTYPE:`, `STATUSFILTER:`, `STATUSPAGE:`, `CMDPAGE:`

**Error handling:** `CallbackHandlerBase.HandleAsync()` **НЕ ловит** исключения — они пропагируются в `CallbackDispatcher.DispatchAsync()`, который ловит `Exception`, логирует с `elapsedMs`/handlerName и возвращает `false` (исключая double logging). `OperationCanceledException` пробрасывается наверх.

**Log instrumentation:** `CallbackDispatcher` логирует dispatch (debug), handled/not-handled (debug с `elapsedMs`), error (error с `elapsedMs`), ignored (debug с reason=no_handler).

Callback prefixes — константы в `CallbackPrefixes` (`TelegramBot.Core/Constants/CallbackPrefixes.cs`). Command codes — в `CommandCodes` (`TelegramBot.Core/Constants/CommandCodes.cs`). Используй `CallbackDataParser.Parse(data)` (from `ParsedCallback.cs`) для получения `ParsedCallback`, затем match через `string.Equals(context.ParsedCallback.Prefix, CallbackPrefixes.GoToParent)`.

> **SessionManagementHandler** управляет `/status` actions. Delete buttons сначала показывают confirmation dialog («✅ Да, удалить» / «↩️ Назад»). Поддерживает фильтры (`ALL`/`ACTIVE`/`DONE`/`FAILED`). Удаление команды (включая running) → `Status = 'Deleted'` (soft-delete). При удалении последней команды в сессии — удаляется и сама сессия + tracked messages (`DeleteSessionAndMessagesAsync`).

For Markdown escaping, use `MarkdownHelper.Escape()` from `TelegramBot.Server/Helpers/`.

### Database

Tables: `BotUsers`, `Sessions`, `Commands`, `TrackedMessages`, `NotificationOutbox`. Все запросы — DB-backed. Soft-delete only — `Status = 'Deleted'`, никогда `DELETE FROM`. Worker auto-cleanup (`SessionCleanupService`) тоже soft-delete для сессий старше `Worker:CompletedSessionRetentionDays` без pending/processing.

**Schemas** (см. [Docs/ExecutionAlgorithm.md](Docs/ExecutionAlgorithm.md#схема-базы-данных) для полной версии):

- **BotUsers** — `UserId BIGINT PK`, `Username TEXT`, `Role INTEGER` (`UserRole` enum: `User=0`, `Admin=1`), `Status INTEGER` (`UserAccessStatus` enum: `Pending=0`, `Approved=1`, `Rejected=2`, `Blocked=3`), `CreatedAt/UpdatedAt TIMESTAMPTZ`
- **Sessions** — `SessionId SERIAL PK`, `UserId BIGINT`, `Username TEXT`, `CorrelationId TEXT NOT NULL`, `PriorityId INTEGER` (default 0), `Status TEXT` (`'pending'`/`'Done'`/`'Failed'`/`'Deleted'`), `ProjectName TEXT`, `FilesAmount INTEGER`, `CompletionNotified BOOLEAN` (default FALSE; защита от duplicate enqueue в multi-worker), `CreatedAt/UpdatedAt TIMESTAMPTZ`
- **Commands** — `CommandId SERIAL PK`, `SessionId INT REFERENCES Sessions`, `CommandText TEXT`, `FilePath TEXT`, `ExecutionOrder INT`, `Status TEXT` (default `'pending'`), `CreatedAt/StartedAt/CompletedAt TIMESTAMPTZ`, `GUID TEXT`, `Lease INT` (Unix seconds), `Partition TEXT`, `Priority INT` (default 5), `ProcessId INT`, `ErrorMessage TEXT`, `RetryCount INT` (default 0), `NextRetryAt TIMESTAMPTZ`, `Progress INT` (default 0), `Result TEXT`, `UpdatedAt TIMESTAMPTZ`
- **TrackedMessages** — `MessageId SERIAL PK`, `SessionId INT REFERENCES Sessions (nullable)`, `ChatId BIGINT`, `MessageIdPg INT`, `CreatedAt TIMESTAMPTZ` (для DB-backed message tracking + cleanup на session delete)
- **NotificationOutbox** — `OutboxId BIGSERIAL PK`, `EventType TEXT` (`session_completed`), `SessionId INT REFERENCES Sessions`, `CorrelationId TEXT`, `Status TEXT` (`pending`/`processing`/`sent`), `Attempts INT`, `NextAttemptAt TIMESTAMPTZ`, `LockedUntil TIMESTAMPTZ`, `LastError TEXT`, `CreatedAt/UpdatedAt/SentAt TIMESTAMPTZ`

**Indexes:** `idx_commands_status`, `idx_commands_session`, `idx_commands_status_lease` (partial WHERE Status='processing'), `idx_sessions_user_created`, `idx_sessions_correlation_id`, `idx_commands_pending_priority` (partial WHERE Status='pending'), `idx_commands_partition_status`, `idx_commands_processing_partition` (partial WHERE Status='processing'), `idx_commands_unique` (UNIQUE on SessionId+CommandText+FilePath), `idx_tracked_messages_session`, `idx_tracked_messages_chat`, `idx_commands_updated_at`, `idx_notification_outbox_session_completed` (UNIQUE partial), `idx_notification_outbox_pending` (pending/processing claim).

**Команды — soft-delete flow:** `CommandDataService.DeleteCommandAsync` → `Status = 'Deleted'` с проверкой `UserId`/`IsAdmin`. `DeleteCommandsByTypeAsync` → soft-delete by `SessionId+CommandType` (исключает уже `Deleted` и `processing`).

**Sessions — soft-delete flow:** `SessionDataService.DeleteSessionAsync` (UserId/IsAdmin) → `Status = 'Deleted'` + soft-delete всех команд. `SoftDeleteInactiveSessionsOlderThanAsync` (cutoff timestamp) → `Status = 'Deleted'` для сессий без `pending`/`processing` команд + cascade soft-delete команд.

**Durable completion notification:** `SessionDataService.NotifySessionCompletedOnceAsync(sessionId, correlationId)` → atomic `UPDATE Sessions SET CompletionNotified=TRUE WHERE SessionId=@Id AND CompletionNotified=FALSE AND Status!='Deleted' RETURNING SessionId` → `INSERT NotificationOutbox(EventType='session_completed')` → `pg_notify('command_completed', SessionId|CorrelationId)` wake-up. `NotificationSenderService` claim'ит outbox через `FOR UPDATE SKIP LOCKED`, отправляет Telegram summary и помечает `sent`; ошибки возвращают запись в `pending` с backoff.

**Session completion summary:** `SessionDataService.GetSessionCompletionSummaryAsync` → UserId/Username/SessionId/CorrelationId/ProjectName + TotalFiles/DoneFiles/FailedFiles + `DurationSeconds = EXTRACT(EPOCH FROM (MAX(CompletedAt) - MIN(StartedAt)))` + `FailedFilePaths` (list).

**`CountPendingProcessingBySessionAsync` (v1.7):** `SessionCompletionTracker` вызывает его после завершения каждой команды; поэтому сессии с числом команд больше `DefaultBatchSize` (5) и несколько воркеров обрабатываются корректно.

**Advisory lock** для `ReleaseExpiredLeasesAsync`: `pg_try_advisory_lock(1234567)` — namespace `telegram_bot_lease_cleanup`. Предотвращает race между несколькими воркерами, освобождающими истёкшие Lease.

Database: **PostgreSQL 18** via Npgsql. Initialized at startup via `host.InitializeDatabaseAsync()` + `host.SeedAdminUsersAsync()` (Server only).
All data access uses **Dapper** (in `TelegramBot.Data/` — `CommandDataService.cs`, `SessionDataService.cs`, `UserDataService.cs`, `MessageTrackingDataService.cs`, `NotificationOutboxDataService.cs`). Connection creation is unified via `CreateOpenConnectionAsync()` helper in `DataAccessBase`. SQL constants в `TelegramBot.Data/Sql/` (6 partial files: `Queries.Schema.cs`, `Queries.Users.cs`, `Queries.Sessions.cs`, `Queries.Commands.cs`, `Queries.TrackedMessages.cs`, `Queries.NotificationOutbox.cs`).

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
| Interfaces | `I` + `PascalCase` | `ICallbackHandler` |
| Methods | `PascalCase` | `HandleCallbackAsync` |
| Async methods | suffix `Async` | `InitializeDatabaseAsync` |
| Private fields | `_camelCase` | `_logger`, `_sessionManager` |
| Properties | `PascalCase` | `SelectedFiles`, `CurrentPath` |
| Local variables | `camelCase` | `chatId`, `sessionId` |
| Parameters | `camelCase` | `userId`, `cancellationToken` |

### Namespace Conventions

Namespaces must match folder structure:
- `TelegramBot.Core.Models`, `TelegramBot.Core.DTOs`, `TelegramBot.Core.Interfaces`, `TelegramBot.Core.Config`, `TelegramBot.Constants`
- `TelegramBot.Core.Helpers`, `TelegramBot.Core.Services`
- `TelegramBot.Data`
- `TelegramBot.Server.Services.Application`, `TelegramBot.Server.Services.Application.Handlers`, `TelegramBot.Server.Services.Infrastructure.Telegram`, `TelegramBot.Server.Services.Infrastructure.FileSystem`, `TelegramBot.Server.Middleware`, `TelegramBot.Server.Helpers`, `TelegramBot.Server.Constants`
- `TelegramBot.Worker.Services`, `TelegramBot.Worker.BimLib.{Config,Models,Monitor,Native,Services}`

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

- All async methods return `Task` or `Task<T>` — never `async void`.
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
- Callback exceptions: `CallbackDispatcher` catches + logs with `elapsedMs`; **`CallbackHandlerBase` does not** (no double logging)
- Worker и Server держат собственные outer retry loops для PostgreSQL LISTEN-соединений

### Logging

- Use `ILogger<T>` injected via constructor (Serilog backs it)
- Use structured logging with message templates — **not** string interpolation:
  ```csharp
  _logger.LogInformation("Received command '{Command}' from {UserId}", command, userId);
  ```
- Log levels: `LogDebug` for diagnostics, `LogInformation` for normal flow, `LogWarning` for recoverable issues, `LogError` / `Log.Fatal` for failures
- BimLib-специфичные логи идут в отдельный файл (`Worker\BimLib\log-{date}.txt`) через вложенную Serilog-конфигурацию в `Worker/Program.cs`

### Collections & Thread Safety

- `UserSession` uses fine-grained locks (`_commandLock`, `_selectionLock`) — follow this pattern for new mutable state
- `RateLimiter` (`Core/Services`) uses `ConcurrentDictionary<long, RequestWindow>` с queue-based sliding window per-user (single lock на уровне `RequestWindow`, без флага `CleanupInProgress`)
- Paths in callback data are passed directly (no `PathMap`/tokens) since v1.1 refactoring
- For new shared dictionaries, prefer `ConcurrentDictionary<,>`
- `SemaphoreSlim` — `SessionManager` хранит per-user `_sessionLocks` (безопасно удаляются с проверкой `CurrentCount == 1`)

### SQL / Data Access (TelegramBot.Data)

- Use `await using var conn = await CreateOpenConnectionAsync()` — connection creation is unified via `DataAccessBase.CreateOpenConnectionAsync()` (protected) or `NpgsqlHelper.CreateOpenConnectionAsync()` (static/public)
- Use **Dapper** for all queries (no raw `NpgsqlCommand`/`NpgsqlDataReader`)
- SQL statements go in verbatim string literals (`@"..."`)
- Use parameterized queries — never string-concatenate user input into SQL
- Soft-delete only: `SET Status = 'Deleted'`, never `DELETE FROM`
- For transactions, use `conn.BeginTransactionAsync()`
- Use `RETURNING` clause for INSERT to get generated IDs (not `last_insert_rowid()`)
- Use `ON CONFLICT DO NOTHING / DO UPDATE` for upserts (not `INSERT OR IGNORE/REPLACE`)
- PostgreSQL data types: `TIMESTAMPTZ` for dates, `SERIAL` for auto-increment, `BIGINT` for user IDs, `INTEGER` for enums, `TEXT` for free-form

### Telegram Messages

- Plain messages via `SendMessageAsync`: `ParseMode.MarkdownV2` — escape via `MarkdownHelper.Escape(text, ParseMode.MarkdownV2)`
- Messages with inline/reply keyboards: `ParseMode.Markdown` — escape via `MarkdownHelper.Escape(text, ParseMode.Markdown)`
- Do not mix the two parse modes
- Completion notifications (via `NotificationSenderService`) отправляются plain text (без parse mode)
- All Telegram API methods must be current — no deprecated approaches

### TelegramBotHostedService — Graceful Shutdown

**Graceful drain:** `_processingCts` is **NOT linked to `stoppingToken`** (was `CreateLinkedTokenSource(stoppingToken)`). Это гарантирует что буферизованные обновления (до 200 в `Channel<Update>`) обрабатываются до shutdown. Правильный порядок:
1. `stoppingToken` cancels → `StartReceiving` останавливает polling
2. `_updateChannel.Writer.TryComplete()` — новые обновления больше не попадают в канал
3. `await processingTask` с 10s timeout — оставшиеся в буфере обновления дренятся
4. `_processingCts.CancelAsync()` — останавливает reader (только если ещё работает)

Previously, linked CTS вызывал немедленное прерывание `ReadAllAsync(ct)` на shutdown и потерю буферизованных обновлений.

### General

- XML doc comments (`/// <summary>`) on new interface methods
- Use `required` keyword on model properties that must always be set
- Prefer `??` and `?? throw new InvalidOperationException(...)` over unchecked null dereferences
- Maintain good code readability and unify methods for easier editing
- Extract shared static helpers (`HandlerHelpers`, `NpgsqlHelper`) when the same 5+ line pattern appears in multiple files
- Use `dotnet format --diagnostics IDE0005` to remove unused `using` directives
- Start notifications are decoupled via `Channel<NotificationItem>` (256-capacity bounded channel). Completion notifications are durable via `NotificationOutbox`; `Channel<NotificationItem>` carries only wake-up signals for outbox drain.
- `NpgsqlHelper.CreateOpenConnectionAsync()` (static, in `TelegramBot.Data`) — public helper for services that don't inherit from `DataAccessBase`

### Optimization Principles

#### Каждый функционал — одна реализация
- ✅ `SendErrorAsync`/`SendNotificationAsync` — удалены
- ✅ `DefaultConnectionString` — вынесен в `DataAccessBase`
- ✅ `SessionDataService`/`CommandDataService` — единая DI-регистрация (обёрнуты в `DataServices` на Server)
- ✅ `_handlerMap` в `CallbackDispatcher` — O(1) lookup вместо O(n) линейного перебора
- ✅ `RevitFileDeduplicator` — extracted из `SlashCommandService` (тестируемо, переиспользуемо)

#### Производительность
- ✅ **Batch-обработка Telegram:** `Parallel.ForEachAsync` с `MaxDegreeOfParallelism=10` + bounded `Channel<Update>` (200)
- ✅ **Drain loop** в `CommandExecutionService` — устраняет head-of-line blocking в Worker
- ✅ **stdout/stderr** через `OutputDataReceived` (lock + 64KB limit + `truncated` flag) — без `BlockingCollection`
- ✅ **Parallel** scan `01_RVT/*.rvt` — `Parallel.ForAsync` по секциям с `FileSystem:RvtScanMaxDegreeOfParallelism` в `SlashCommandService.CollectRvtFilesAsync`
- ✅ **Markdown-экранирование:** `Regex.Replace`
- ✅ **FS-caching:** TTL-кэш в `FileSystemBrowser` (5 сек)
- ✅ **Session cleanup:** lazy при доступе + фоновая раз в 30 мин
- ✅ **Atomic writes** для task/result XML: `.tmp` → `File.Move(overwrite: true)`
- ✅ **`sessionRemaining`** — in-memory счётчик, уменьшает SQL-запросы
- ✅ **Durable completion notifications** — `NotificationOutbox` + `pg_notify` wake-up + polling fallback

#### Интерфейсы
- ✅ Все single-implementation удалены (кроме `ICallbackHandler` и `ITelegramOutputService`, data services)
- Не создавай новый интерфейс, если не планируется ≥2 реализаций

---

## Known Issues (Do Not Worsen)

- `.editorconfig` exists with naming rules, formatting preferences, and `generated_code = true` markers for specific files — `dotnet format --verify-no-changes` passes with **exit code 0** (full compliance)
- **VSTHRD analyzer** (`Microsoft.VisualStudio.Threading.Analyzers`) is enabled globally via `Directory.Build.props` with `WarningsAsErrors` for 8 codes (VSTHRD002/003/100-104/200). All violations have been fixed or suppressed with justified `#pragma` — `dotnet build` produces **0 VSTHRD errors**
- CI pipeline exists (`.github/workflows/ci.yml`) — runs `dotnet build` and `dotnet publish` on push/PR. No automated tests — the only verification is a successful `dotnet build`
- Keep secrets out of committed config files — use `TelegramBot.Server/appsettings.Local.json` (gitignored) or env var `TelegramBot__Token`; never hardcode tokens
- PostgreSQL connection string in committed `appsettings.json` uses default `postgres/postgres` credentials — override via `appsettings.Local.json` or env var `ConnectionStrings__Postgres`
- **PostgreSQL major-version upgrades** (e.g. 17 → 18 in `docker-compose.yml`) требуют `docker compose down -v` для volume `pgdata` либо отдельной миграции через `pg_upgrade` — данные из старой major-версии не читаются новой. Перед изменением `image: postgres:*` в compose — предупреди пользователя о потере данных.
- **Interfaces remaining:** `ICallbackHandler` (6 implementations), `ITelegramOutputService` (sole consumer interface)

---

## Residual architectural concerns

См. подробности в [Docs/CriticalReview.md](Docs/CriticalReview.md):
- **At-least-once Telegram delivery boundary** — если Telegram send уже прошёл, но Server упал до `NotificationOutbox.Status='sent'`, summary может отправиться повторно после retry
- **Single-Writer assumption на Server** — в multi-instance сценарии потребуется distributed lock или ownership для outbox sender'а

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **TelegramBot** (1277 symbols, 3294 relationships, 104 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

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
