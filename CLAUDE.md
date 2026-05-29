# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Build all projects
dotnet build TelegramBot.sln

# Run
dotnet run --project TelegramBot.Server/TelegramBot.Server.csproj

# Publish
dotnet publish TelegramBot.Server/TelegramBot.Server.csproj -c Release
```

There are no automated tests in this project.

## Project Structure

3 projects in `TelegramBot.sln`:

| Project | Purpose | Dependencies |
|---|---|---|
| `TelegramBot.Core` | Models, DTOs, interfaces, config | None (no Telegram SDK) |
| `TelegramBot.Data` | SQLite persistence (Dapper) | Core |
| `TelegramBot.Server` | Telegram bot, handlers, hosting | Core + Data |

The old `TelegramBotServer/` directory contains the legacy single-project code and is no longer used.

## Configuration

Bot token and root path are **not** hardcoded. Set them in `TelegramBot.Server/appsettings.Local.json` (gitignored):

```json
{
  "TelegramBot": { "Token": "<your-bot-token>" },
  "FileSystem": { "RootPath": "B:\\" },
  "ConnectionStrings": { "Sqlite": "Data Source=botdata.db" }
}
```

`TelegramBot:Token` can also be set via the environment variable `TelegramBot__Token`. The app throws on startup if the token is missing.

## Architecture

### Request Flow

```
Telegram API -> TelegramBotHostedService (polling)
             -> TelegramUpdateMapper (Update -> MessageDto | CallbackQueryDto)
             -> CommandAppService.HandleUserCommandAsync / HandleCallbackAsync
             -> ICallbackDispatcher -> CallbackDispatcher.DispatchAsync (Chain of Responsibility)
```

All services are registered as **Singletons** via `DependencyInjectionExtensions.cs`.

**`TelegramBotHostedService`** — Entry point. Registers bot commands, starts polling, routes incoming updates. Uses `ISessionManager.AcquireUserLockAsync()` for per-user concurrency control.

**`CommandAppService`** — Handles text commands (`/export`, `/automation`, `/status`, `/help`). Delegates all inline keyboard callbacks to `ICallbackDispatcher`.

**`CallbackDispatcher`** — Chain of Responsibility dispatcher implementing `ICallbackDispatcher`. Routes callback queries to the first `ICallbackHandler` that `CanHandle()` the prefix. Handlers sorted by `Priority` (lower = first):

| Handler | Priority | Prefixes |
|---|---|---|
| `FileNavigationHandler` | 10 | OPENFOLDER:, GOTOPARENT: |
| `FileSelectionHandler` | 20 | FILE:, APPLYFILES:, CANCELFILESEL: |
| `ExportCommandHandler` | 100 | PDF:, DWG:, NWC:, IFC: |
| `AutomationCommandHandler` | 100 | BIMDOC:, CLASHREP:, AUTORES: |
| `SessionManagementHandler` | 100 | SESSIONDETAILS:, DELETESESSION:, DELETECOMMAND:, BACKTOSTATUS: |
| `CommandSelectionHandler` | 100 | SELMODE:, APPLYCOMMANDS:, CANCELCOMMANDSSEL: |

Use `CallbackDataParser.Parse(callbackData)` to get a `ParsedCallback` struct, then match with `parsed.Is(CallbackPrefixes.OpenFolder)`.

### Key Services

| Class | Location | Responsibility |
|---|---|---|
| `FileSystemBrowser` | Server/Infrastructure | Builds inline keyboards for filesystem navigation |
| `KeyboardBuilder` | Server/Infrastructure | Context-aware keyboards with selection state |
| `SessionManager` | Server/Application | In-memory sessions (`ConcurrentDictionary`, 5-min timeout) |
| `TelegramOutputService` | Server/Infrastructure | Send/edit Telegram messages with retry |
| `SqliteDataService` | Data | All DB persistence via Dapper |

### Database

Tables: `Sessions`, `Commands`. Soft-delete only — rows are never physically removed.
DB file: `botdata.db`. Initialized at startup via `host.InitializeDatabaseAsync()`.
All queries use Dapper with parameterized SQL.

### Logging

Serilog configured via `appsettings.json`. Supports Console and Seq (`http://localhost:5341`) sinks.

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **TelegramBot** (731 symbols, 1944 relationships, 61 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

> If any GitNexus tool warns the index is stale, run `npx gitnexus analyze` in terminal first.

## Always Do

- **MUST run impact analysis before editing any symbol.** Before modifying a function, class, or method, run `gitnexus_impact({target: "symbolName", direction: "upstream"})` and report the blast radius (direct callers, affected processes, risk level) to the user.
- **MUST run `gitnexus_detect_changes()` before committing** to verify your changes only affect expected symbols and execution flows.
- **MUST warn the user** if impact analysis returns HIGH or CRITICAL risk before proceeding with edits.
- When exploring unfamiliar code, use `gitnexus_query({query: "concept"})` to find execution flows instead of grepping. It returns process-grouped results ranked by relevance.
- When you need full context on a specific symbol — callers, callees, which execution flows it participates in — use `gitnexus_context({name: "symbolName"})`.

## Never Do

- NEVER edit a function, class, or method without first running `gitnexus_impact` on it.
- NEVER ignore HIGH or CRITICAL risk warnings from impact analysis.
- NEVER rename symbols with find-and-replace — use `gitnexus_rename` which understands the call graph.
- NEVER commit changes without running `gitnexus_detect_changes()` to check affected scope.

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
