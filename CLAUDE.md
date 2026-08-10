# TelegramBot — AI quick reference

Полные правила: [AGENTS.md](AGENTS.md). При конфликте — код.

## Проверка

```powershell
dotnet build TelegramBot.slnx
```

Тесты не добавлять.

## Ключевые invariants

- 4 проекта net10.0: Core ← Data; Server/Worker зависят от Core+Data
- Windows-only; PostgreSQL 18, Dapper/Npgsql
- Soft-delete (`Status='Deleted'`), кроме `TrackedMessages` (physical DELETE)
- DI services — singleton; callback handlers регистрируются через `CallbackHandlerBase`
- `Async` suffix, без `async void`/sync-over-async/`ConfigureAwait(false)`
- Worker: tracked tasks + SQL partition scheduling
- Revit: TaskFile через `REVITBIMFUSION_TASK_FILE`, без контрактных CLI-аргументов (`/language RUS` допустим)
- `IsRevitCommand()`: PDF, DWG, NWC, DATA, IFC
- BIM contract: эталон `RevitBIMFusion/Docs/BimPluginContract.md` (v2026-08-10); XSD vendored в `Docs/BimContract/`

## GitNexus

1. `node .gitnexus/run.cjs analyze` при stale index
2. `impact` → `detect_changes` → build

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **TelegramBot** (1191 symbols, 3129 relationships, 96 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

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
