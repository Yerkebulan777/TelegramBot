# TelegramBot — AI quick reference

Полные правила: [AGENTS.md](AGENTS.md). При конфликте — код.

## Проверка

```powershell
dotnet build TelegramBot.slnx
```

Тесты не добавлять.

## Invariants

- 5 проектов net10.0: Core ← Data; Server/Worker/RootPathSetup → Core+Data
- Windows-only; PostgreSQL 18; Dapper/Npgsql; soft-delete (`Deleted`), кроме `TrackedMessages`
- DI — singleton; handlers через `CallbackHandlerBase` (6 шт., включая `RootPath`)
- Outbox уведомлений — polling 3 с; cleanup по `DeleteAfter`/`NextDeleteAttemptAt`; completion защищён; ack+tracking атомарно
- Worker: poll 1 с, partition scheduling, `ProcessLaunchGate` ≥ 15 с (Revit и AutoCAD независимо); `CommandPersistenceException` ≠ BIM-ошибка
- Revit: `REVITBIMFUSION_TASK_FILE`; `RequiresRevit`: PDF, DWG, NWC, DATA, IFC, RESAVE
- `MERGEDWG`: AutoCAD + AutoBIMFusion; путь DWG как DrawingExportModule (`02_DWG/.../{RevitFileName}/`)
- `RootPathSetup` — заявка 30 мин; активный корень меняет только admin в Telegram
- `Async` suffix; без `async void` / sync-over-async / `ConfigureAwait(false)`
- BIM: эталон v2026-09-09; XSD в `Docs/BimContract/`

## GitNexus

1. `node .gitnexus/run.cjs analyze` при stale index  
2. `impact` → `detect_changes` → build  

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
