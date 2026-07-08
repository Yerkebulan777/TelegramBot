# AGENTS.md

Инструкции для coding agents в этом репозитории.

## Источники истины

| Документ / код | Назначение |
|---|---|
| [README.md](README.md) | запуск, команды, конфигурация |
| [Docs/ExecutionAlgorithm.md](Docs/ExecutionAlgorithm.md) | pipeline, статусы, retry, уведомления, БД |
| [Docs/RevitCrashes.md](Docs/RevitCrashes.md) | историческое расследование Revit |
| [BimPluginContract.md](https://github.com/Yerkebulan777/RevitBIMFusion/blob/master/Docs/BimPluginContract.md) | эталон TaskFile/ResultFile |
| `TelegramBot.Data/Sql/` | фактическая схема и SQL |
| option-классы `Config/` | defaults конфигурации |

## Build

```powershell
dotnet build TelegramBot.slnx
dotnet run --project TelegramBot.Server/TelegramBot.Server.csproj
dotnet run --project TelegramBot.Worker/TelegramBot.Worker.csproj
dotnet publish TelegramBot.Server/TelegramBot.Server.csproj -c Release
dotnet format TelegramBot.slnx
```

Тесты не добавлять. После изменений — полная сборка.

## Архитектура

Проекты — [README.md](README.md). Ключевые принципы:
- DI services — singleton. Hosted services — владеет host.
- BimLib встроен в Worker, Windows-only.

## Server flow

```text
Telegram SDK → Channel<Update> (200) → Parallel.ForEachAsync (max 10) → TelegramUpdateMapper
→ SessionManager per-user lock → CommandAppService → SlashCommandService / CallbackDispatcher → ICallbackHandler
```

- Разные пользователи — параллельно, один пользователь — последовательно
- `/start`, `REQACCESS:`, `APPROVEUSER:`, `REJECTUSER:` без access check
- `CallbackDispatcher`: dictionary префикс → handler

### Команды и callbacks

`CommandCatalog`:

| Группа | Коды |
|---|---|
| Export | `PDF`, `DWG`, `NWC`, `DATA`, `IFC` |
| Automation | `BIMDOC`, `CLASHREP`, `AUTORES` |

Handlers и их prefixes — в `TelegramBot.Core/Constants/CallbackPrefixes.cs`.

### File selection

`FileSystemBrowser` — три уровня: RootPath (проекты, single-select) → 01_PROJECT (разделы) → 01_RVT (файлы, multi-select). Синхронное сканирование с кэшированием. Фильтрация: `.rvt` > 50 MiB, допустимое имя, дедупликация.

### Server DI

`AddTelegramBotServer` регистрирует: 6 `ICallbackHandler`, `CallbackDispatcher`, `CommandAppService`, `AuthorizationMiddleware`, `RateLimiter`, `SlashCommandService`, `SessionManager` (idle 5 мин), `SessionsListRenderer`, `MessageTrackingService`, data services, `FileSystemBrowser`, `ITelegramBotClient`, `TelegramOutputService`, `TelegramUpdateMapper`, `KeyboardBuilder`, `Channel<NotificationItem>(256)`, `TelegramBotHostedService`, `CommandNotificationService`, `NotificationSenderService`.

## Worker flow

```text
LISTEN new_tasks → DrainPendingCommands → ClaimPendingCommands → ProcessRunner.RunAsync
→ CommandPreparer.PrepareAsync → ProcessStarter.StartAsync → OutputCollector + ResultAnalyzer → Done/retry/Failed
```

Ограничение: tracked running tasks + SQL partition scheduling (одна команда на Partition за раз).

### Worker DI

`Program.cs` регистрирует: `CommandDataService`, `SessionDataService`, `WorkerOptions`, `FileSystemOptions`, `BimIntegrationOptions`, `DialogDismisserOptions`, `ExportFolderCleanupOptions`, `RevitVersionDetector`, `NavisworksPathResolver`, `RevitPathResolver`, `DialogDismisser`, `CommandPreparer`, `ProcessStarter`, `OutputCollector`, `ResultAnalyzer`, `ProcessRunner`, `CommandExecutionService`, `SessionCleanupService`, `ExportFolderCleanupService`.

## BIM-контракт

См. [README.md](README.md) (#bim-контракт). Локальную копию контракта не создавать.

## PostgreSQL invariants

- Parameterized Dapper queries
- Только soft-delete (`Status = 'Deleted'`). `DELETE` только для `TrackedMessages`
- Статусы: `pending`, `processing`, `Done`, `Failed`, `Deleted`
- Claim: `FOR UPDATE SKIP LOCKED` + partition advisory xact lock
- Lease cleanup и outbox sender — под session advisory locks
- Session + Commands — в одной транзакции с user-level xact lock

## Logging

- Structured templates: `logger.LogInformation("Command done: id={CommandId}", id)`
- Без интерполяции, `IsEnabled()` для дорогих вычислений
- `Debug` — циклические события, `Information` — milestone, `Warning` — recoverable, `Error` — потеря операции
- IDs: `CommandId`, `SessionId`, `CorrelationId`, `UserId`, `ProcessId`
- Не писать token, password, содержимое файлов

## Code style

- C# 12+/net10.0, nullable + implicit usings
- Primary constructors допустимы
- Concrete class > интерфейс (кроме `ICallbackHandler`)
- `Async` suffix, без `async void`/sync-over-async/`ConfigureAwait(false)`
- Constants из `TelegramBot.Core/Constants`
- Path input: `Path.GetFullPath`, `IsPathWithinRoot`, allowed extensions, reparse-point guard
- Минимальный diff, без speculative abstractions

## Documentation policy

1. commands/config/behavior → README
2. pipeline/schema/SQL/status/retry → ExecutionAlgorithm.md
3. TaskFile/ResultFile/startup → сначала canonical RevitBIMFusion/Docs/BimPluginContract.md
4. архитектура/DI/agent rules → AGENTS.md + CLAUDE.md
5. исторический incident — не переписывать

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **TelegramBot** (1243 symbols, 3277 relationships, 100 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

> Index stale? Run `node .gitnexus/run.cjs analyze` from the project root.

## Always Do

- **MUST run `impact` on any symbol before editing it** — report blast radius (callers, processes, risk level).
- **MUST warn user** on HIGH/CRITICAL risk before editing.
- **MUST run `detect_changes(scope: "all")` before committing.**
- Use `query` → `context` for exploring; `rename` for refactoring.

## Never Do

- NEVER edit without `impact` first.
- NEVER ignore HIGH/CRITICAL risk.
- NEVER rename with find/replace.
- NEVER commit without `detect_changes`.

## CLI

| Task | Skill |
|---|---|
| Architecture / "How does X work?" | `gitnexus-exploring` |
| Blast radius / "What breaks if I change X?" | `gitnexus-impact-analysis` |
| Debug / "Why is X failing?" | `gitnexus-debugging` |
| Rename/refactor | `gitnexus-refactoring` |
| Tools reference | `gitnexus-guide` |
| Index/status/clean | `gitnexus-cli` |

<!-- gitnexus:end -->
