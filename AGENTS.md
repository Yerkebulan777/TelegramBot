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

## Ключевые файлы

| Файл | Назначение |
|---|---|
| `TelegramBot.Core/Constants/` | `CommandCodes.cs`, `CallbackPrefixes.cs`, `Statuses.cs`, `CommandPriorities.cs` |
| `TelegramBot.Core/Config/` | `BotOptions.cs`, `FileSystemOptions.cs`, `RateLimitOptions.cs`, `WorkerOptions.cs`, `CommandConfig.cs` |
| `TelegramBot.Core/Models/` | `UserSession.cs`, `PendingCommand.cs`, `BotUser.cs`, `UserRole.cs` |
| `TelegramBot.Data/Sql/Queries.*.cs` | SQL-запросы (Schema, Commands, Sessions, NotificationOutbox, TrackedMessages, Users) |
| `TelegramBot.Server/Services/Application/Handlers/` | 6 `ICallbackHandler`: `AccessRequest`, `FileNavigation`, `FileSelection`, `CommandToggle`, `CommandSelection`, `SessionManagement` |
| `TelegramBot.Server/Services/Infrastructure/Telegram/` | `TelegramBotHostedService.cs`, `TelegramOutputService.cs`, `KeyboardBuilder.cs`, `CommandNotificationService.cs`, `NotificationSenderService.cs` |
| `TelegramBot.Server/Services/Infrastructure/FileSystem/FileSystemBrowser.cs` | 3-уровневая навигация + кэширование |
| `TelegramBot.Server/Extensions/DependencyInjectionExtensions.cs` | Server DI |
| `TelegramBot.Worker/Services/` | `CommandExecutionService.cs`, `CommandPreparer.cs`, `ProcessStarter.cs`, `ProcessRunner.cs`, `OutputCollector.cs`, `ResultAnalyzer.cs`, `ErrorClassifier.cs`, `SessionCleanupService.cs` |
| `TelegramBot.Worker/BimLib/` | `RevitVersionDetector.cs`, `NavisworksPathResolver.cs`, `DialogDismisser.cs` |
| `TelegramBot.Worker/Program.cs` | Worker DI + startup |

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

`Program.cs` регистрирует: `CommandDataService`, `SessionDataService`, `WorkerOptions`, `FileSystemOptions`, `BimIntegrationOptions`, `DialogDismisserOptions`, `RevitVersionDetector`, `NavisworksPathResolver`, `RevitPathResolver`, `DialogDismisser`, `CommandPreparer`, `ProcessStarter`, `OutputCollector`, `ResultAnalyzer`, `ProcessRunner`, `CommandExecutionService`, `SessionCleanupService`.

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

This project is indexed by GitNexus as **TelegramBot** (1244 symbols, 3258 relationships, 100 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

> Index stale? Run `node .gitnexus/run.cjs analyze` from the project root — it auto-selects an available runner. No `.gitnexus/run.cjs` yet? `npx gitnexus analyze` (npm 11 crash → `npm i -g gitnexus`; #1939).

## Always Do

- **MUST run impact analysis before editing any symbol.** Before modifying a function, class, or method, run `impact({target: "symbolName", direction: "upstream"})` and report the blast radius (direct callers, affected processes, risk level) to the user.
- **MUST run `detect_changes()` before committing** to verify your changes only affect expected symbols and execution flows. For regression review, compare against the default branch: `detect_changes({scope: "compare", base_ref: "master"})`.
- **MUST warn the user** if impact analysis returns HIGH or CRITICAL risk before proceeding with edits.
- When exploring unfamiliar code, use `query({search_query: "concept"})` to find execution flows instead of grepping. It returns process-grouped results ranked by relevance.
- When you need full context on a specific symbol — callers, callees, which execution flows it participates in — use `context({name: "symbolName"})`.
- For security review, `explain({target: "fileOrSymbol"})` lists taint findings (source→sink flows; needs `analyze --pdg`).

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
