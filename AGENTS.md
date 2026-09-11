# AGENTS.md

Инструкции для coding agents. При конфликте — код.

## Источники истины

| Документ / код | Назначение |
|---|---|
| [README.md](README.md) | запуск, деплой, конфигурация |
| [Docs/ExecutionAlgorithm.md](Docs/ExecutionAlgorithm.md) | pipeline, статусы, retry, уведомления, БД |
| [Docs/ADR.md](Docs/ADR.md) | принятые архитектурные решения |
| [BimPluginContract.md](https://github.com/Yerkebulan777/RevitBIMFusion/blob/master/Docs/BimPluginContract.md) | TaskFile/ResultFile (v2026-09-09); локально — `../RevitBIMFusion/Docs` |
| `Docs/BimContract/` | vendored XSD |
| `TelegramBot.Data/Sql/` | схема и SQL |
| `TelegramBot.Core/Config/` | defaults конфигурации |

## Структура

| Путь | Роль |
|---|---|
| `TelegramBot.Core/` | константы, модели, options, traits |
| `TelegramBot.Data/` | Dapper/Npgsql, SQL |
| `TelegramBot.Server/` | Telegram UI, callbacks, outbox, cleanup, иконка статуса в трее |
| `TelegramBot.Worker/` | очередь, процессы, BimLib |
| `TelegramBot.RootPathSetup/` | WinForms: заявка на смену UNC (30 мин) |
| `Installer/PostgresConnectionCheck/` | `check` / `ensure` PostgreSQL 18 в Docker |

## Build

```powershell
dotnet build TelegramBot.slnx
dotnet run --project TelegramBot.Server/TelegramBot.Server.csproj
dotnet run --project TelegramBot.Worker/TelegramBot.Worker.csproj
dotnet format TelegramBot.slnx
```

Тесты не добавлять. После изменений — полная сборка.

## Архитектура

- 5 проектов net10.0, Windows-only (Server и RootPathSetup — `net10.0-windows`). DI services — singleton; hosted — владеет host.
- BimLib встроен в Worker.

### Server

```text
Telegram SDK → Channel (200) → Parallel.ForEachAsync (max 10)
→ SessionManager (per-user lock) → SlashCommand / CallbackDispatcher → CallbackHandlerBase
```

Разные пользователи параллельно, один — последовательно. Без access check: `/start`, `REQACCESS:`, `APPROVEUSER:`, `REJECTUSER:`.

Handlers: `FileNavigation`, `FileSelection`, `CommandToggle`, `CommandSelection`, `SessionManagement`, `RootPath`. Prefixes — `CallbackPrefixes`.

| Группа | Коды |
|---|---|
| Export | `PDF`, `DWG`, `NWC`, `DATA`, `IFC` |
| Automation | `CLASHREP` (FileConvert; planned Navisworks AddIn), `RESAVE`, `MERGEDWG` (AutoCAD + AutoBIMFusion) |

`FileSystemBrowser`: RootPath → `01_PROJECT` → `01_RVT` (`.rvt` > 50 MiB). Hosted: `ServerTrayHostedService` (иконка в трее), `ServerHealthCheckService` (БД+Telegram, 15 с), `DatabaseInitializerService` (фон, не блокирует старт), `TelegramBotHostedService`, `NotificationSenderService` (outbox 3 с), `TrackedMessageCleanupService`.

### Worker

```text
Polling (1s) → lease cleanup → ClaimPendingCommands → Prepare → Start → Result → Done/retry/Failed
```

Ограничения: `MaxConcurrentCommands` + одна команда на Partition. `ProcessLaunchGate` — ≥ 15 с между глобальными `Process.Start()` Revit и (отдельно) AutoCAD; выбор gate — `CommandTraits.GetLaunchGate`. `CommandPersistenceException` — не BIM-ошибка; ResultFile сохранять при сбое записи.

`CommandTraits.RequiresRevit`: PDF, DWG, NWC, DATA, IFC, RESAVE. TaskFile — `REVITBIMFUSION_TASK_FILE` (без контрактных CLI; `/language RUS` допустим).

`MERGEDWG`: RVT → папка `{Base}/02_DWG/{relative?}/{RevitFileName}/` (как DrawingExportModule); `.scr` + status JSON; `acad.exe /nologo /b`.

## PostgreSQL

- Parameterized Dapper; soft-delete (`Status='Deleted'`); физический `DELETE` только `TrackedMessages`
- Статусы: `pending`, `processing`, `Done`, `Failed`, `Deleted`
- Claim: `FOR UPDATE SKIP LOCKED` + partition advisory xact lock
- Lease cleanup, outbox, terminal completion — session advisory locks
- Session + Commands — одна транзакция с user-level xact lock

## Logging и стиль

- Structured templates, без интерполяции; `IsEnabled()` для дорогих вычислений
- IDs: `CommandId`, `SessionId`, `CorrelationId`, `UserId`, `ProcessId`; не логировать token/password/содержимое файлов
- C# 12+/net10.0, nullable; concrete class > интерфейс; `Async` suffix; без `async void` / sync-over-async / `ConfigureAwait(false)`
- Пути: `Path.GetFullPath`, `IsPathWithinRoot`, allowed extensions, reparse-point guard
- Минимальный diff

## Documentation policy

1. запуск/деплой/config → README  
2. pipeline/SQL/status/retry → ExecutionAlgorithm.md  
3. TaskFile/ResultFile → канонический BimPluginContract + `Docs/BimContract/`  
4. архитектура/правила агентов → AGENTS.md + CLAUDE.md  
5. решения → ADR.md  

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **TelegramBot** (1407 symbols, 3499 relationships, 113 execution flows).

> Index stale? Run `node .gitnexus/run.cjs analyze --index-only` from the project root — it auto-selects an available runner. No `.gitnexus/run.cjs` yet? Bootstrap with `npx`, `bunx`, or `pnpm dlx` — e.g. `bunx gitnexus@latest analyze` (npm 11 npx crash; #1939).

## Always Do

- **MUST run impact before editing.** Use `impact({target: "symbolName", direction: "upstream"})` or `node .gitnexus/run.cjs impact "symbolName" --direction upstream --repo .`; report callers, processes, and risk. Never substitute grep for graph analysis.
- **MUST analyze graph changes before committing.** Use `detect_changes({scope: "all"})` (MCP) or `node .gitnexus/run.cjs detect-changes --scope all --repo .` (CLI fallback). `partial: true` or `truncated: true` is not a clean check — a zero means unseen, not unaffected; re-run it. For regression review: `detect_changes({scope: "compare", base_ref: "master"})` or `node .gitnexus/run.cjs detect-changes --scope compare --base-ref "master" --repo .`.
- MUST warn on HIGH/CRITICAL `risk` pre-edit; never use `riskSharedAxes` to waive a HIGH/CRITICAL `risk` warning. Compare File/symbol: MCP File omits axes; Graph-RAG expands File.
- **MUST treat `risk: UNKNOWN` as unresolved, not as low.** An empty caller set is not evidence the symbol is unused — it can also mean the callers are not resolvable by the index (plain-object property access, dynamic dispatch, cross-language calls). `impact` pairs `UNKNOWN` with a `riskNote` saying so. Confirm with a text search before treating the symbol as safe to change or delete; do not proceed on the strength of a zero.
- **MUST use `query({search_query: "concept"})` for concepts/flows, `context({name: "symbolName"})` for a named symbol, or `impact` for blast radius, on read-only callers, dependencies, imports, or execution flow.** Graph first; text search only for empty/`UNKNOWN`/literals.
- For security review, `explain({target: "fileOrSymbol"})` lists taint findings (source→sink flows; needs `analyze --pdg`).

## Never Do

- NEVER edit a function, class, or method before MCP/CLI impact analysis.
- NEVER ignore HIGH or CRITICAL risk warnings from impact analysis, and never read `UNKNOWN` as an all-clear — it means the walk could not answer, which is the one verdict that requires confirming by other means.
- NEVER rename symbols with find-and-replace — use `rename` which understands the call graph.
- NEVER commit before MCP/CLI graph change analysis.

## Resources

| Resource | Use for |
| --- | --- |
| `gitnexus://repo/TelegramBot/context` | Codebase overview, check index freshness |
| `gitnexus://repo/TelegramBot/clusters` | All functional areas |
| `gitnexus://repo/TelegramBot/processes` | All execution flows |
| `gitnexus://repo/TelegramBot/process/{name}` | Step-by-step execution trace |

## CLI

| Task | Read this skill file |
| --- | --- |
| Understand architecture / "How does X work?" | `.claude/skills/gitnexus-exploring/SKILL.md` |
| Blast radius / "What breaks if I change X?" | `.claude/skills/gitnexus-impact-analysis/SKILL.md` |
| Trace bugs / "Why is X failing?" | `.claude/skills/gitnexus-debugging/SKILL.md` |
| Rename / extract / split / refactor | `.claude/skills/gitnexus-refactoring/SKILL.md` |
| Tools, resources, schema reference | `.claude/skills/gitnexus-guide/SKILL.md` |
| Index, status, clean, wiki CLI commands | `.claude/skills/gitnexus-cli/SKILL.md` |

<!-- gitnexus:end -->
