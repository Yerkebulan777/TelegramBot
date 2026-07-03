# TelegramBot — Project Instructions for AI Agents

## Quick Reference

| Документ | Описание |
|----------|----------|
| [AGENTS.md](AGENTS.md) | Основной файл: архитектура, DI, BimLib, code style, константы (включая «Accepted design constraints») |
| [README.md](README.md) | Обзор, команды, конфигурация, запуск |
| [Docs/ExecutionAlgorithm.md](Docs/ExecutionAlgorithm.md) | Алгоритм выполнения команд, SQL-запросы, схема БД |
| [Docs/BimPluginContract.md](Docs/BimPluginContract.md) | Контракт BIM-плагинов |

## Build & Verify

```bash
# Build all projects (main verification)
dotnet build TelegramBot.slnx

# Run Server
dotnet run --project TelegramBot.Server/TelegramBot.Server.csproj

# Run Worker (separate terminal)
dotnet run --project TelegramBot.Worker/TelegramBot.Worker.csproj

# Publish
dotnet publish TelegramBot.Server/TelegramBot.Server.csproj -c Release

# Format code (must pass: exit code 0)
dotnet format TelegramBot.slnx
```

**Tests are intentionally disabled.** Do not add test projects or run `dotnet test`.

## Key Architecture Rules

- **4 projects** (`.slnx`): Core ← Data → Server + Worker. All services are **Singletons**.
- **Windows-only**: BimLib uses Registry + P/Invoke. `[SupportedOSPlatform("windows")]` everywhere.
- **PostgreSQL 18** via Dapper + Npgsql. Use `await using var conn = await CreateOpenConnectionAsync()`.
- **Soft-delete only**: `Status = 'Deleted'`, never `DELETE FROM`.
- **No single-implementation interfaces** (except `ICallbackHandler` and `ITelegramOutputService`).
- **Primary constructors** preferred (C# 12). No redundant `private readonly` fields for direct captures.
- **Async methods** always suffixed with `Async`, no `async void`, no `ConfigureAwait(false)`.
- **Worker staggering**: `ProcessRunner._launchGate` serializes `Process.Start()` with `LaunchStaggerSeconds` (default 30s) to prevent Revit CEF port collision.
- **Revit handoff**: TaskFile path is passed per process through `REVITBIMFUSION_TASK_FILE`; the real command lives in `TaskFile.commandText`.

## Documentation Updates

When changing code, keep docs in sync:
1. Update `AGENTS.md` if architecture, DI, handlers, or constants change
2. Update `Docs/ExecutionAlgorithm.md` if SQL queries, DB schema, or pipeline changes
3. Update `Docs/BimPluginContract.md` if TaskFile/ResultFile, ArgumentsTemplate, or contract changes
4. Update the «Accepted design constraints» section in `AGENTS.md` if introducing or removing a by-design limitation
5. Update `README.md` if config, commands, or general overview changes

All docs live in the repo root and `Docs/` — keep path references consistent.

---

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **TelegramBot** (1283 symbols, 3363 relationships, 104 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

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
