# AGENTS.md

Инструкции для coding agents в этом репозитории.

## Источники истины

| Документ / код | Назначение |
|---|---|
| [README.md](README.md) | запуск, команды, конфигурация |
| [Docs/ExecutionAlgorithm.md](Docs/ExecutionAlgorithm.md) | очередь, статусы, retry, уведомления, БД |
| [Docs/RevitCrashes.md](Docs/RevitCrashes.md) | историческое расследование Revit |
| [BimPluginContract.md](https://github.com/Yerkebulan777/RevitBIMFusion/blob/master/Docs/BimPluginContract.md) | единственный эталон TaskFile/ResultFile и BIM-исполнителей |
| `TelegramBot.Data/Sql/` | фактическая схема и SQL |
| `appsettings.json` + option-классы | фактическая конфигурация и defaults |

Historical specs в `Docs/superpowers/specs/` фиксируют решения на дату создания и не заменяют operational docs.

## Build

```powershell
dotnet build TelegramBot.slnx
dotnet run --project TelegramBot.Server/TelegramBot.Server.csproj
dotnet run --project TelegramBot.Worker/TelegramBot.Worker.csproj
dotnet publish TelegramBot.Server/TelegramBot.Server.csproj -c Release
dotnet format TelegramBot.slnx
```

Тесты намеренно отключены. Не добавлять test projects и не запускать `dotnet test`. После изменений обязательна полная сборка `dotnet build TelegramBot.slnx`.

## Архитектура

```text
TelegramBot.Core   ← TelegramBot.Data
       ↑                    ↑
       ├── TelegramBot.Server
       └── TelegramBot.Worker
                └── BimLib/
```

- `Core`: модели, DTO, options, constants, helpers. Нет зависимости от Telegram SDK.
- `Data`: PostgreSQL 18 через Dapper/Npgsql. Ссылается только на Core.
- `Server`: generic Worker host, Telegram long-polling, application handlers, notifications.
- `Worker`: PostgreSQL scheduler и внешние BIM/AI-процессы.
- BimLib встроен в Worker и Windows-only.
- DI services — singleton; исключение по смыслу только hosted services, которыми владеет host.

## Server flow

```text
Telegram SDK
→ bounded Channel<Update> (200)
→ Parallel.ForEachAsync (max 10)
→ TelegramUpdateMapper
→ SessionManager per-user lock
→ CommandAppService
→ SlashCommandService или CallbackDispatcher
→ ICallbackHandler
```

- Обновления разных пользователей обрабатываются параллельно, одного пользователя — последовательно.
- `/start` допускается без approved access.
- `REQACCESS:`, `APPROVEUSER:`, `REJECTUSER:` обходят обычную access-проверку.
- Остальные команды и callbacks требуют `BotUsers.Status = Approved`.
- `CallbackDispatcher` строит dictionary префикс → handler и запрещает дубли.

### Команды и callbacks

`CommandCatalog` — единый каталог пользовательских команд:

| Группа | Коды |
|---|---|
| Export | `PDF`, `DWG`, `NWC`, `DATA`, `IFC` |
| Automation | `BIMDOC`, `CLASHREP`, `AUTORES` |

Handlers:

| Handler | Prefixes |
|---|---|
| `AccessRequestHandler` | `REQACCESS:`, `APPROVEUSER:`, `REJECTUSER:` |
| `FileNavigationHandler` | `GOTOPARENT:` (не используется, dead code) |
| `FileSelectionHandler` | `FILE:`, `SELECTALLSECTIONS:`, `OPENFOLDER:` |
| `CommandToggleHandler` | command prefixes из `CommandCatalog` |
| `CommandSelectionHandler` | `APPLYCOMMANDS:`, `CANCELCOMMANDSSEL:` |
| `SessionManagementHandler` | status/page/details/delete prefixes |

Все callback prefixes определены в `TelegramBot.Core/Constants/CallbackPrefixes.cs` и заканчиваются `:`.

### File selection

`FileSystemBrowser` — три уровня навигации, определяются структурой `CurrentPath`:

- `RootPath` — каталоги, совпадающие с `SectionFolderPattern` и содержащие `ProjectDirectoryName` (single-select, клик = выбор проекта);
- `<project>/01_PROJECT` — каталоги разделов с известным section acronym (клик = `OPENFOLDER:` — переход к файлам раздела, выбор не сбрасывается);
- `<project>/01_PROJECT/<section>` — список `.rvt`-файлов раздела (multi-select чекбоксами + кнопка "Выбрать все").

Список файлов раздела строит `FileSystemBrowser` синхронным сканированием `01_RVT`: сначала верхний уровень (depth 0), а если валидных файлов нет — прямые субпапки (depth 1); глубже не углубляется. Фильтрация и дедупликация те же: `.rvt` больше 50 MiB с допустимым именем через `RevitFileDeduplicator`. Кнопка "Выбрать все" (`SELECTALLSECTIONS:`) доступна только на уровне файлов одного раздела (`FileSystemBrowser.GetSelectableFiles` возвращает файлы только текущего раздела) — массовый скан всех разделов проекта разом намеренно убран как слишком накладный по I/O. `SlashCommandService.ConfirmFileSelectionAsync` на финальном шаге просто берёт `session.GetSelectedFiles()` — файлы уже отобраны и провалидированы на этапе навигации, повторное сканирование не требуется. Имя проекта для лога/уведомления берётся из пути первого выбранного файла (`GetProjectName`), а не из `session.CurrentPath` — CurrentPath после `OPENFOLDER:` указывает вглубь на раздел, а не на проект.

### Server DI

`DependencyInjectionExtensions.AddTelegramBotServer` регистрирует:

- 6 `ICallbackHandler`, `CallbackDispatcher`;
- `CommandAppService`, `AuthorizationMiddleware`, `RateLimiter`, `SlashCommandService`;
- `SessionManager` с idle timeout 5 минут, `SessionsListRenderer`, `MessageTrackingService`;
- Data services, `DatabaseInitializerService`, `FileSystemBrowser`;
- `ITelegramBotClient`, `TelegramOutputService`, `TelegramUpdateMapper`, `KeyboardBuilder`;
- bounded `Channel<NotificationItem>` capacity 256;
- `TelegramBotHostedService`, `CommandNotificationService`, `NotificationSenderService`.

Server при старте инициализирует схему БД и upsert-ит configured admins.

## Worker flow

```text
LISTEN new_tasks / fallback polling
→ CommandExecutionService.DrainPendingCommandsAsync
→ CommandDataService.ClaimPendingCommandsAsync
→ ProcessRunner.RunAsync
→ CommandPreparer.PrepareAsync
→ ProcessStarter.StartAsync
→ OutputCollector + ResultAnalyzer
→ Done / retry / Failed
→ completion outbox
```

### Ответственность компонентов

| Компонент | Ответственность |
|---|---|
| `CommandExecutionService` | LISTEN reconnect, fallback polling, claim до свободных slots, tracking tasks, lease cleanup, health monitoring, shutdown |
| `CommandPreparer` | command lookup, path validation, executable resolution, TaskFile/ProcessStartInfo, temp cleanup |
| `ProcessStarter` | атомарное создание TaskFile, сериализованный `Process.Start`, запись `ProcessId` |
| `OutputCollector` | bounded stdout/stderr: 64 KiB capture, 4 KiB log preview |
| `ResultAnalyzer` | ResultFile parse/delete, `.bad` для invalid XML, exit-code fallback |
| `ProcessRunner` | timeout, retry/fail transitions, active process registry, session completion |
| `SessionCleanupService` | soft-delete старых inactive sessions |

`MaxConcurrentCommands` ограничивается количеством tracked running tasks. SQL claim возвращает не более одной команды на `Partition`; partition строится из нормализованного `FilePath`, поэтому команды одного файла выполняются последовательно.

`ProcessStarter._launchGate` сериализует только момент `Process.Start()`. Дополнительной stagger-задержки в текущем коде нет.

### Worker DI

Worker регистрирует:

- `CommandDataService`, `SessionDataService`;
- `WorkerOptions`, `FileSystemOptions`, `BimIntegrationOptions`, `DialogDismisserOptions`;
- `RevitVersionDetector`, `RevitPathResolver`, `NavisworksPathResolver`, `DialogDismisser`;
- `CommandPreparer`, `ProcessStarter`, `OutputCollector`, `ResultAnalyzer`, `ProcessRunner`;
- `CommandExecutionService`, `SessionCleanupService`.

## BimLib и BIM-контракт

BimLib находится в `TelegramBot.Worker/BimLib/`:

- `Services`: Revit file version detection и registry executable lookup;
- `Monitor`: process health и optional dialog dismissal;
- `Native`: безопасные Win32 wrappers;
- `Config` / `Models`: options и health/version models.

Worker поддерживает `.rvt`, `.rfa`, `.rte` для чтения `BasicFileInfo`; запуск ограничен `BimIntegrationOptions.MinSupportedVersion..MaxSupportedVersion`.

Контрактные invariants:

- canonical contract и обе XSD находятся только в соседнем `RevitBIMFusion/Docs`;
- Worker встраивает canonical `TaskFile.schema.xsd` при сборке;
- другой путь задаётся `BIM_CONTRACT_DIRECTORY`;
- Revit получает абсолютный TaskFile path только через process-scoped `REVITBIMFUSION_TASK_FILE`;
- у Revit-команд пустые CLI arguments;
- Revit ResultFile обязателен; exit code fallback разрешён только wrapper/console-командам;
- task/result XML пишутся атомарно и удаляются best-effort;
- invalid result XML переименовывается в `.bad`.

При изменении границы обновляются canonical contract, XSD, плагин и Worker. Локальную копию контракта не создавать.

## PostgreSQL invariants

- Только parameterized Dapper queries.
- `DataAccessBase.CreateOpenConnectionAsync()` — для data services; public `NpgsqlHelper` — для non-data listeners.
- Сессии и команды удаляются только через `Status = 'Deleted'`; SQL `DELETE` не использовать.
- Статусы: `pending`, `processing`, `Done`, `Failed`, `Deleted`.
- Финальные: `Done`, `Failed`, `Deleted`; active: `pending`, `processing`.
- `SessionDataService.CreateSessionWithCommandsAsync` создаёт session + commands в одной транзакции.
- User-level advisory xact lock закрывает гонку duplicate check/insert.
- Claim использует `FOR UPDATE SKIP LOCKED` и partition advisory xact lock.
- Lease cleanup защищён session advisory lock `1_234_567`.
- Outbox sender защищён session advisory lock `1_234_569`.
- Completion notification создаётся атомарно один раз вместе с `NotificationOutbox`.

Подробности и схема — [Docs/ExecutionAlgorithm.md](Docs/ExecutionAlgorithm.md); SQL не дублировать в документации.

## Logging

- Использовать structured templates: `logger.LogInformation("Command done: id={CommandId}", id)`.
- Не использовать interpolation/concatenation внутри вызова logger.
- Не вычислять `string.Join`, LINQ, serialization или большие `ToString()` при выключенном log level; ставить `logger.IsEnabled(...)` или логировать count.
- В цикле собирать одну bounded запись через `StringBuilder`, если отдельные события не несут самостоятельной ценности.
- Нормальные повторяющиеся события — `Debug` или без лога; `Information` — lifecycle/business milestone; `Warning` — recoverable anomaly; `Error` — потерянная операция.
- Сохранять релевантные IDs: `CommandId`, `SessionId`, `CorrelationId`, `UserId`, `ProcessId`.
- Не писать Telegram token, connection-string password и содержимое пользовательских файлов.

## Code style

- C# 12+/net10.0, nullable и implicit usings включены.
- Primary constructors допустимы; не копировать direct captures в поля без необходимости.
- Concrete class вместо интерфейса с одной реализацией; `ICallbackHandler` остаётся полиморфной точкой.
- Async methods заканчиваются `Async`; запрет `async void`, sync-over-async и `ConfigureAwait(false)` в app code.
- CancellationToken передавать по существующей цепочке; shutdown cancellation не логировать как ошибку.
- Constants брать из `TelegramBot.Core/Constants`.
- Path input проверять через `Path.GetFullPath`, `IsPathWithinRoot`, allowed extensions и reparse-point guard.
- Для shared mutable state использовать существующие per-user locks/concurrent collections; новые locks добавлять только при доказанной гонке.
- Минимальный diff, без speculative abstractions и новых dependencies.

## Documentation policy

При изменении:

1. команд/config/general behavior — обновить `README.md`;
2. pipeline/schema/SQL/status/retry — обновить `Docs/ExecutionAlgorithm.md`;
3. TaskFile/ResultFile/startup handoff — сначала canonical `RevitBIMFusion/Docs/BimPluginContract.md`;
4. архитектуры/DI/constants/agent rules — обновить `AGENTS.md` и при необходимости `CLAUDE.md`;
5. исторический incident не переписывать как текущую архитектуру; добавить короткий current-status note.

## GitNexus — Code Intelligence

Проект индексируется как `TelegramBot`.

Перед изменением метода/класса:

1. проверить свежесть индекса; при отставании выполнить `node .gitnexus/run.cjs analyze`;
2. выполнить upstream `impact` для изменяемого symbol;
3. предупредить пользователя до правки при HIGH/CRITICAL risk;
4. после правок выполнить `detect_changes(scope: "all")`;
5. проверить затронутые execution flows и затем собрать solution.

Для архитектуры использовать `query` → `context`; для rename/refactor — соответствующий GitNexus skill. Не делать symbol rename простым find/replace.

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **TelegramBot** (1245 symbols, 3287 relationships, 100 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

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
